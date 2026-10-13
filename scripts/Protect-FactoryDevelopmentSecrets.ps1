# Restrict the two private development directories to the current operator, SYSTEM and Administrators.
# No global ACL, account or service changes. Run as the same operator who starts the local services.
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath("$PSScriptRoot/..")
foreach($relative in @('artifacts/keycloak','artifacts/review-postgres')) {
    $path=Join-Path $repo $relative
    if(-not (Test-Path $path)){New-Item -ItemType Directory -Path $path -Force | Out-Null}
    # Preserve owner/group and audit metadata; modify only the discretionary access rules.
    $directory=[IO.DirectoryInfo]::new($path)
    $acl=$directory.GetAccessControl([Security.AccessControl.AccessControlSections]::Access)
    $acl.SetAccessRuleProtection($true,$false)
    foreach($existing in @($acl.Access)){$acl.RemoveAccessRuleSpecific($existing)}
    foreach($sid in @([Security.Principal.WindowsIdentity]::GetCurrent().User,
        [Security.Principal.SecurityIdentifier]::new('S-1-5-18'),
        [Security.Principal.SecurityIdentifier]::new('S-1-5-32-544'))) {
        $rule=[Security.AccessControl.FileSystemAccessRule]::new($sid,'FullControl','ContainerInherit,ObjectInherit','None','Allow')
        $acl.AddAccessRule($rule)
    }
    $directory.SetAccessControl($acl)
}
Write-Output 'Private development directories restricted to the operator, SYSTEM and Administrators.'
