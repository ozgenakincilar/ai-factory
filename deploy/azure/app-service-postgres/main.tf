# Module app-service-postgres 1.0.0: one ASP.NET Core API on a Linux App Service (F1) with an Azure
# Database for PostgreSQL flexible server (B1MS) and a Key Vault holding the connection string.
# The database password is ephemeral and written only through write-only arguments, so it is
# never stored in the plan or the state.

data "azurerm_client_config" "current" {}

data "azurerm_resource_group" "target" {
  name = var.resource_group_name
}

locals {
  short = substr(replace(var.application_name, "-", ""), 0, 12)
  tags = {
    "factory-application" = var.application_name
    "factory-environment" = var.environment
    "factory-module"      = "app-service-postgres/1.0.0"
  }
  database_login = "factoryadmin"
}

ephemeral "random_password" "database" {
  length  = 32
  special = false
}

resource "azurerm_postgresql_flexible_server" "database" {
  name                              = "${var.application_name}-${var.environment}-pg-${var.name_suffix}"
  resource_group_name               = data.azurerm_resource_group.target.name
  location                          = var.location
  version                           = "17"
  sku_name                          = "B_Standard_B1ms"
  storage_mb                        = 32768
  backup_retention_days             = 7
  geo_redundant_backup_enabled      = false
  public_network_access_enabled     = true
  administrator_login               = local.database_login
  administrator_password_wo         = ephemeral.random_password.database.result
  administrator_password_wo_version = var.database_password_version
  tags                              = local.tags

  authentication {
    password_auth_enabled         = true
    active_directory_auth_enabled = false
  }

  lifecycle {
    ignore_changes = [zone]
  }
}

resource "azurerm_postgresql_flexible_server_database" "application" {
  name      = "app"
  server_id = azurerm_postgresql_flexible_server.database.id
  charset   = "UTF8"
  collation = "en_US.utf8"
}

# The F1 plan has no virtual network integration; the server accepts Azure-internal addresses only
# (the 0.0.0.0 rule), with TLS required. See docs/DEPLOYMENT.md for the limits of this choice.
resource "azurerm_postgresql_flexible_server_firewall_rule" "azure" {
  name             = "AllowAzureServices"
  server_id        = azurerm_postgresql_flexible_server.database.id
  start_ip_address = "0.0.0.0"
  end_ip_address   = "0.0.0.0"
}

resource "azurerm_key_vault" "secrets" {
  name                       = "kv${local.short}${substr(var.environment, 0, 3)}${var.name_suffix}"
  resource_group_name        = data.azurerm_resource_group.target.name
  location                   = var.location
  tenant_id                  = data.azurerm_client_config.current.tenant_id
  sku_name                   = "standard"
  rbac_authorization_enabled = false
  purge_protection_enabled   = false
  soft_delete_retention_days = 7
  tags                       = local.tags
}

# Access policies instead of RBAC: the deployment identity is a Contributor on the resource group and
# may not create role assignments.
resource "azurerm_key_vault_access_policy" "deployer" {
  key_vault_id       = azurerm_key_vault.secrets.id
  tenant_id          = data.azurerm_client_config.current.tenant_id
  object_id          = data.azurerm_client_config.current.object_id
  secret_permissions = ["Get", "List", "Set", "Delete", "Recover"]
}

resource "azurerm_key_vault_secret" "database" {
  name             = "database-connection"
  key_vault_id     = azurerm_key_vault.secrets.id
  content_type     = "Npgsql connection string"
  value_wo         = "Host=${azurerm_postgresql_flexible_server.database.fqdn};Database=${azurerm_postgresql_flexible_server_database.application.name};Username=${local.database_login};Password=${ephemeral.random_password.database.result};Ssl Mode=Require"
  value_wo_version = var.database_password_version
  tags             = local.tags

  depends_on = [azurerm_key_vault_access_policy.deployer]
}

resource "azurerm_service_plan" "application" {
  name                = "${var.application_name}-${var.environment}-plan-${var.name_suffix}"
  resource_group_name = data.azurerm_resource_group.target.name
  location            = var.location
  os_type             = "Linux"
  sku_name            = "F1"
  tags                = local.tags
}

resource "azurerm_linux_web_app" "api" {
  name                                           = "${var.application_name}-${var.environment}-${var.name_suffix}"
  resource_group_name                            = data.azurerm_resource_group.target.name
  location                                       = var.location
  service_plan_id                                = azurerm_service_plan.application.id
  https_only                                     = true
  ftp_publish_basic_authentication_enabled       = false
  webdeploy_publish_basic_authentication_enabled = true
  zip_deploy_file                                = var.release_package
  tags                                           = local.tags

  identity {
    type = "SystemAssigned"
  }

  site_config {
    always_on           = false
    ftps_state          = "Disabled"
    minimum_tls_version = "1.2"
    http2_enabled       = true
    application_stack {
      dotnet_version = "10.0"
    }
  }

  app_settings = {
    "WEBSITE_RUN_FROM_PACKAGE"    = "1"
    "ConnectionStrings__Database" = "@Microsoft.KeyVault(SecretUri=${azurerm_key_vault_secret.database.versionless_id})"
    "FACTORY_RELEASE_SHA256"      = var.release_sha256
    "ASPNETCORE_ENVIRONMENT"      = "Staging"
  }
}

resource "azurerm_key_vault_access_policy" "api" {
  key_vault_id       = azurerm_key_vault.secrets.id
  tenant_id          = azurerm_linux_web_app.api.identity[0].tenant_id
  object_id          = azurerm_linux_web_app.api.identity[0].principal_id
  secret_permissions = ["Get"]
}
