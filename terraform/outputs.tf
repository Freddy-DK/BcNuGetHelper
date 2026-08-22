output "function_app_name" {
  value = azurerm_function_app_flex_consumption.func.name
}

output "function_app_url" {
  value = "https://${azurerm_function_app_flex_consumption.func.default_hostname}"
}

output "storage_account_name" {
  value = azurerm_storage_account.packages.name
}
