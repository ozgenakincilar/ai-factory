variable "resource_group_name" {
  description = "Existing resource group of the deployment target (the deployment identity is scoped to it)."
  type        = string
}

variable "location" {
  description = "Azure region; must equal the resource group's region."
  type        = string
}

variable "application_name" {
  description = "Kebab-case application name, for example stock-flow."
  type        = string
  validation {
    condition     = can(regex("^[a-z][a-z0-9-]{1,30}[a-z0-9]$", var.application_name))
    error_message = "application_name must be 3-32 lowercase letters, digits or hyphens."
  }
}

variable "environment" {
  description = "Deployment environment."
  type        = string
  validation {
    condition     = contains(["staging"], var.environment)
    error_message = "Only the staging environment is supported by module 1.0.0."
  }
}

variable "name_suffix" {
  description = "Deterministic 8-hex suffix that makes the global names unique per target."
  type        = string
  validation {
    condition     = can(regex("^[0-9a-f]{8}$", var.name_suffix))
    error_message = "name_suffix must be 8 lowercase hex characters."
  }
}

variable "release_package" {
  description = "Path of the release zip inside the Terraform working directory; it embeds the package hash."
  type        = string
}

variable "release_sha256" {
  description = "SHA-256 of the release package, recorded as an app setting."
  type        = string
  validation {
    condition     = can(regex("^[0-9a-f]{64}$", var.release_sha256))
    error_message = "release_sha256 must be a lowercase SHA-256."
  }
}

variable "database_password_version" {
  description = "Increase to rotate the database password (write-only; never stored in state)."
  type        = number
  default     = 1
}
