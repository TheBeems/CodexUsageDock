function Assert-LocalTestPolicy {
    # Registration alone does not establish that Windows will allow unsigned
    # development binaries, including DLLs loaded by the COM server, to run.
    $policyPath = 'HKLM:/SYSTEM/CurrentControlSet/Control/CI/Policy'
    if (-not (Test-Path -LiteralPath $policyPath -ErrorAction Stop)) { return }
    $policy = Get-ItemProperty -LiteralPath $policyPath -ErrorAction Stop
    $state = $policy.PSObject.Properties['VerifiedAndReputablePolicyState']
    if ($null -ne $state -and $state.Value -eq 1) {
        throw 'Smart App Control is enforcing trusted code. This development workflow produces unsigned binaries that Windows can block, even with Developer Mode enabled. No installation change was made. Use a trusted signed build or a compatible isolated development environment. To restore the signed Store version, run scripts/test-local.ps1 -RestoreStore. See DEVELOPMENT.md#smart-app-control.'
    }
}
