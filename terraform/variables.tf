variable "base_name" {
  description = "Base name for all resources. Lowercase letters and digits only, 3-17 characters, globally unique."
  type        = string

  validation {
    condition     = can(regex("^[a-z][a-z0-9]{2,16}$", var.base_name))
    error_message = "base_name must be 3-17 lowercase letters/digits, starting with a letter."
  }
}

variable "location" {
  description = "Azure region to deploy to."
  type        = string
  default     = "westeurope"
}

variable "resource_group_name" {
  description = "Name of the (pre-created) resource group to deploy into."
  type        = string
}

variable "public_feeds" {
  description = "Comma-separated list of feeds (apps, runtime, symbols) served without authentication. Empty means all feeds are private."
  type        = string
  default     = ""
}
