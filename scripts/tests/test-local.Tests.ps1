. (Join-Path $PSScriptRoot '../test-local.ps1')

Describe 'Local test installation' {
    BeforeEach {
        $script:package = [pscustomobject]@{
            PackageFullName = 'TheBeems.CodexUsageDock_0.8.1.0_arm64__qye81p6cmqsf6'
            PackageFamilyName = $localTestPackageFamily
            InstallLocation = 'C:/Program Files/WindowsApps/CodexUsageDock'
            IsDevelopmentMode = $false
            SignatureKind = 'Store'
        }
        Mock Get-AppxPackage { $script:package }
        Mock Assert-LocalTestPolicy {}
        Mock Get-LocalTestWinget { 'winget.exe' }
        Mock Stop-LocalTestProvider {}
        Mock New-LocalTestBackup { @() }
        Mock Restore-LocalTestData {}
        Mock Invoke-LocalTestBuild {}
        Mock Invoke-LocalTestPreflight {}
        Mock Remove-AppxPackage { $script:package = $null }
        Mock Add-AppxPackage {}
        Mock Install-LocalTestStore {}
    }

    It 'does not uninstall when the build fails' {
        Mock Invoke-LocalTestBuild { throw 'Build failed' }
        { Invoke-LocalTest ARM64 } | Should Throw
        Assert-MockCalled -Scope It Remove-AppxPackage -Times 0 -Exactly
    }

    It 'does not uninstall stale or invalid artifacts even when skipping the build' {
        Mock Invoke-LocalTestPreflight { throw 'Stale build' }
        { Invoke-LocalTest ARM64 -NoBuild } | Should Throw
        Assert-MockCalled -Scope It Remove-AppxPackage -Times 0 -Exactly
    }

    It 'refuses an incompatible security policy before any installation work' {
        Mock Assert-LocalTestPolicy { throw 'Smart App Control is enforcing trusted code.' }
        { Invoke-LocalTest ARM64 } | Should Throw 'Smart App Control'
        Assert-MockCalled -Scope It Stop-LocalTestProvider -Times 0 -Exactly
        Assert-MockCalled -Scope It Invoke-LocalTestBuild -Times 0 -Exactly
        Assert-MockCalled -Scope It Invoke-LocalTestPreflight -Times 0 -Exactly
        Assert-MockCalled -Scope It New-LocalTestBackup -Times 0 -Exactly
        Assert-MockCalled -Scope It Remove-AppxPackage -Times 0 -Exactly
        Assert-MockCalled -Scope It Install-LocalTestStore -Times 0 -Exactly
    }

    It 'also checks the policy when reusing an existing build' {
        Mock Assert-LocalTestPolicy { throw 'Smart App Control is enforcing trusted code.' }
        { Invoke-LocalTest ARM64 -NoBuild } | Should Throw 'Smart App Control'
        Assert-MockCalled -Scope It Invoke-LocalTestPreflight -Times 0 -Exactly
        Assert-MockCalled -Scope It Remove-AppxPackage -Times 0 -Exactly
    }

    It 'does not uninstall if the backup fails' {
        Mock New-LocalTestBackup { throw 'Backup unavailable' }
        { Invoke-LocalTest ARM64 -NoBuild } | Should Throw
        Assert-MockCalled -Scope It Remove-AppxPackage -Times 0 -Exactly
    }

    It 'replaces the Store package only after validation and backup' {
        Mock Remove-AppxPackage {
            Assert-MockCalled -Scope It Invoke-LocalTestPreflight -Times 1 -Exactly -ParameterFilter { -not $Register }
            Assert-MockCalled -Scope It New-LocalTestBackup -Times 1 -Exactly
            $script:package = $null
        }
        Invoke-LocalTest ARM64 -NoBuild
        Assert-MockCalled -Scope It Remove-AppxPackage -Times 1 -Exactly -ParameterFilter { -not $PreserveApplicationData }
        Assert-MockCalled -Scope It Invoke-LocalTestPreflight -Times 1 -Exactly -ParameterFilter { $Register }
        Assert-MockCalled -Scope It Restore-LocalTestData -Times 1 -Exactly
    }

    It 'recovers the Store registration when local registration fails' {
        Mock Invoke-LocalTestPreflight { if ($Register) { throw 'Registration failed' } }
        { Invoke-LocalTest ARM64 -NoBuild } | Should Throw
        Assert-MockCalled -Scope It Install-LocalTestStore -Times 1 -Exactly
    }

    It 'refreshes an existing repository development package in place' {
        $script:package.IsDevelopmentMode = $true
        $script:package.InstallLocation = Join-Path $localTestRepo 'CodexUsageDock/bin/ARM64/Debug'
        Invoke-LocalTest ARM64 -NoBuild
        Assert-MockCalled -Scope It Remove-AppxPackage -Times 0 -Exactly
        Assert-MockCalled -Scope It Invoke-LocalTestPreflight -Times 1 -Exactly -ParameterFilter { $Register }
    }

    It 'refuses a development package from another checkout before mutation' {
        $script:package.IsDevelopmentMode = $true
        $script:package.InstallLocation = Join-Path $TestDrive 'another-checkout'
        { Invoke-LocalTest ARM64 -NoBuild } | Should Throw
        Assert-MockCalled -Scope It Remove-AppxPackage -Times 0 -Exactly
        Assert-MockCalled -Scope It Stop-LocalTestProvider -Times 0 -Exactly
    }

    It 'preserves development data when returning to the Store' {
        $script:package.IsDevelopmentMode = $true
        $script:package.InstallLocation = Join-Path $localTestRepo 'CodexUsageDock/bin/ARM64/Debug'
        Invoke-LocalTest ARM64 -UseStore
        Assert-MockCalled -Scope It Remove-AppxPackage -Times 1 -Exactly -ParameterFilter { $PreserveApplicationData }
        Assert-MockCalled -Scope It Install-LocalTestStore -Times 1 -Exactly
        Assert-MockCalled -Scope It Assert-LocalTestPolicy -Times 0 -Exactly
    }

    It 'does nothing when the Store version is already registered' {
        Invoke-LocalTest ARM64 -UseStore
        Assert-MockCalled -Scope It Remove-AppxPackage -Times 0 -Exactly
        Assert-MockCalled -Scope It New-LocalTestBackup -Times 0 -Exactly
    }
}

Describe 'Smart App Control development policy check' {
    BeforeEach {
        Mock Test-Path { $true }
        Mock Get-ItemProperty { [pscustomobject]@{ VerifiedAndReputablePolicyState = 1 } }
    }

    It 'rejects enforcement without modifying policy' {
        { Assert-LocalTestPolicy } | Should Throw 'Smart App Control'
    }

    It 'allows evaluation mode' {
        Mock Get-ItemProperty { [pscustomobject]@{ VerifiedAndReputablePolicyState = 2 } }
        { Assert-LocalTestPolicy } | Should Not Throw
    }

    It 'allows disabled policy' {
        Mock Get-ItemProperty { [pscustomobject]@{ VerifiedAndReputablePolicyState = 0 } }
        { Assert-LocalTestPolicy } | Should Not Throw
    }

    It 'supports Windows versions without the policy key' {
        Mock Test-Path { $false }
        { Assert-LocalTestPolicy } | Should Not Throw
        Assert-MockCalled -Scope It Get-ItemProperty -Times 0 -Exactly
    }

    It 'supports a policy key without the Smart App Control value' {
        Mock Get-ItemProperty { [pscustomobject]@{} }
        { Assert-LocalTestPolicy } | Should Not Throw
    }

    It 'does not silently approve an unreadable policy' {
        Mock Get-ItemProperty { throw 'Access denied' }
        { Assert-LocalTestPolicy } | Should Throw 'Access denied'
    }
}

Describe 'Local data backup' {
    It 'retains settings and nested history independently of the application directory' {
        $root = Join-Path $TestDrive 'data'
        $source = Join-Path $root 'CodexUsageDock/contexts/test'
        New-Item -ItemType Directory -Path $source -Force | Out-Null
        'history fixture' | Set-Content (Join-Path $source 'weekly-usage-history.json')
        $copies = New-LocalTestBackup $root $null
        'changed history' | Set-Content (Join-Path $source 'weekly-usage-history.json')
        Restore-LocalTestData $copies
        (Get-Content (Join-Path $source 'weekly-usage-history.json')) | Should Be 'history fixture'
        $copies.Count | Should Be 1
        $copies[0].Backup.StartsWith((Join-Path $root 'CodexUsageDock-development-backups')) | Should Be $true
    }
}
