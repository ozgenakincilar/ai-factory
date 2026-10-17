terraform {
  required_version = "= 1.16.5"

  required_providers {
    azurerm = {
      source  = "hashicorp/azurerm"
      version = "= 5.8.0"
    }
    random = {
      source  = "hashicorp/random"
      version = "= 3.9.1"
    }
  }

  # Filled at init time with -backend-config (storage account, container, key); Entra ID auth only.
  backend "azurerm" {
    use_azuread_auth = true
  }
}

provider "azurerm" {
  features {
    key_vault {
      purge_soft_delete_on_destroy = false
    }
  }
  # The deployment identity is scoped to one resource group and cannot register providers;
  # scripts/Initialize-AzureStaging.ps1 registers them once.
  resource_provider_registrations = "none"
  storage_use_azuread             = true
}
