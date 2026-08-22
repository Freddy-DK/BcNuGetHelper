# The resource group is created by the deploy workflow bootstrap step
# (it also holds the Terraform state storage account).
data "azurerm_resource_group" "rg" {
  name = var.resource_group_name
}

resource "azurerm_storage_account" "packages" {
  name                            = var.base_name
  resource_group_name             = data.azurerm_resource_group.rg.name
  location                        = var.location
  account_tier                    = "Standard"
  account_replication_type        = "LRS"
  min_tls_version                 = "TLS1_2"
  allow_nested_items_to_be_public = false
  shared_access_key_enabled       = false
}

resource "azurerm_storage_container" "packages" {
  name                  = "packages"
  storage_account_id    = azurerm_storage_account.packages.id
  container_access_type = "private"
}

resource "azurerm_storage_container" "deployments" {
  name                  = "deployments"
  storage_account_id    = azurerm_storage_account.packages.id
  container_access_type = "private"
}

resource "azurerm_user_assigned_identity" "func" {
  name                = "${var.base_name}-id"
  resource_group_name = data.azurerm_resource_group.rg.name
  location            = var.location
}

# Gives the function app access to packages and deployment artifacts
resource "azurerm_role_assignment" "func_storage" {
  scope                = azurerm_storage_account.packages.id
  role_definition_name = "Storage Blob Data Contributor"
  principal_id         = azurerm_user_assigned_identity.func.principal_id
}

resource "azurerm_log_analytics_workspace" "log" {
  name                = "${var.base_name}-log"
  resource_group_name = data.azurerm_resource_group.rg.name
  location            = var.location
  sku                 = "PerGB2018"
  retention_in_days   = 30
}

resource "azurerm_application_insights" "ai" {
  name                = "${var.base_name}-ai"
  resource_group_name = data.azurerm_resource_group.rg.name
  location            = var.location
  workspace_id        = azurerm_log_analytics_workspace.log.id
  application_type    = "web"
}

resource "azurerm_service_plan" "plan" {
  name                = "${var.base_name}-plan"
  resource_group_name = data.azurerm_resource_group.rg.name
  location            = var.location
  os_type             = "Linux"
  sku_name            = "FC1"
}

resource "azurerm_function_app_flex_consumption" "func" {
  name                = "${var.base_name}-func"
  resource_group_name = data.azurerm_resource_group.rg.name
  location            = var.location
  service_plan_id     = azurerm_service_plan.plan.id

  storage_container_type            = "blobContainer"
  storage_container_endpoint        = "${azurerm_storage_account.packages.primary_blob_endpoint}${azurerm_storage_container.deployments.name}"
  storage_authentication_type       = "UserAssignedIdentity"
  storage_user_assigned_identity_id = azurerm_user_assigned_identity.func.id

  runtime_name           = "dotnet-isolated"
  runtime_version        = "10.0"
  instance_memory_in_mb  = 2048
  maximum_instance_count = 40

  https_only = true

  identity {
    type         = "UserAssigned"
    identity_ids = [azurerm_user_assigned_identity.func.id]
  }

  site_config {
    application_insights_connection_string = azurerm_application_insights.ai.connection_string
  }

  app_settings = {
    PackagesStorageAccountName = azurerm_storage_account.packages.name
    AZURE_CLIENT_ID            = azurerm_user_assigned_identity.func.client_id

    AzureWebJobsStorage__accountName = azurerm_storage_account.packages.name
    AzureWebJobsStorage__credential  = "managedidentity"
    AzureWebJobsStorage__clientId    = azurerm_user_assigned_identity.func.client_id
  }

  depends_on = [azurerm_role_assignment.func_storage]
}
