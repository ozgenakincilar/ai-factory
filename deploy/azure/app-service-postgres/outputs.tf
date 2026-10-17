output "url" {
  description = "HTTPS base address of the deployed API."
  value       = "https://${azurerm_linux_web_app.api.default_hostname}"
}

output "web_app_name" {
  value = azurerm_linux_web_app.api.name
}

output "database_server" {
  value = azurerm_postgresql_flexible_server.database.name
}
