# Installs hippo's native binary from a GitHub release on Windows, after checking it against the release's SHA256SUMS:
#
#   irm https://raw.githubusercontent.com/alisterpineda/hippo/main/install.ps1 | iex
#
# HIPPO_VERSION        the version to install, such as 0.1.0 or a prerelease such as 0.1.0-rc.1; the latest release
#                      otherwise
# HIPPO_INSTALL_DIR    where hippo.exe goes; %LOCALAPPDATA%\Programs\hippo otherwise. The directory is added to the user
#                      PATH.
# HIPPO_DOWNLOAD_BASE  replaces https://github.com/alisterpineda/hippo/releases/download; CI points it at a local
#                      server, which may be plain HTTP
#
# Runs on Windows PowerShell 5.1, which every Windows install has and where `irm | iex` usually lands, and on pwsh 7.
# The asset names and the SHA256SUMS format are the release's asset contract, in docs/dev/releasing.md. Everything
# runs inside one script block, so a download cut short leaves it unclosed and runs nothing. `iex` runs the script in
# the user's session, where the block's functions and settings end with it, except the TLS 1.2 flag below, which
# belongs to the process and stays added.

& {
    function Install-Hippo {
        $ErrorActionPreference = 'Stop'
        # Windows PowerShell 5.1 draws a progress bar that slows Invoke-WebRequest down many times over.
        $ProgressPreference = 'SilentlyContinue'
        # Windows PowerShell 5.1 may not offer TLS 1.2, which GitHub requires.
        [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

        $repo = 'https://github.com/alisterpineda/hippo'

        # PROCESSOR_ARCHITEW6432 is the machine's architecture when a 32-bit PowerShell runs on 64-bit Windows.
        $arch = if ($env:PROCESSOR_ARCHITEW6432) { $env:PROCESSOR_ARCHITEW6432 } else { $env:PROCESSOR_ARCHITECTURE }
        switch ($arch) {
            'AMD64' { $rid = 'win-x64' }
            'ARM64' { throw 'hippo has no Windows arm64 binary yet; install it as a .NET tool: dotnet tool install -g hippo' }
            default { throw "hippo has no binary for Windows $arch; see Install in the README for other ways to get it: $repo#install" }
        }

        # The latest release is read from the redirect GitHub answers releases/latest with, which ends in its tag.
        # Unlike the REST API, it is not metered, and it skips prereleases.
        if ($env:HIPPO_VERSION) {
            $version = $env:HIPPO_VERSION -replace '^v', ''
        } else {
            $version = Get-LatestVersion "$repo/releases/latest"
            if (-not $version) { throw 'could not find the latest release; set HIPPO_VERSION' }
        }

        $asset = "hippo-$version-$rid.zip"
        $base = if ($env:HIPPO_DOWNLOAD_BASE) { $env:HIPPO_DOWNLOAD_BASE } else { "$repo/releases/download" }
        $base = "$base/v$version"

        $tmp = Join-Path ([IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString())
        New-Item -ItemType Directory -Path $tmp | Out-Null
        try {
            Write-Host "Downloading $asset"
            $status = Save-Download "$base/$asset" (Join-Path $tmp $asset)
            switch ($status) {
                200 {}
                404 { throw "release v$version has no $asset; check HIPPO_VERSION, or that v$version is a release" }
                default { throw "could not download $base/${asset}: HTTP $status" }
            }
            $sums = Join-Path $tmp 'SHA256SUMS'
            $status = Save-Download "$base/SHA256SUMS" $sums
            if ($status -ne 200) { throw "could not download $base/SHA256SUMS: HTTP $status" }

            $expected = $null
            foreach ($line in Get-Content $sums) {
                $fields = -split $line
                if ($fields.Count -ge 2 -and $fields[1] -ceq $asset) {
                    $expected = $fields[0]
                    break
                }
            }
            if (-not $expected) { throw "SHA256SUMS in release v$version has no line for $asset; nothing was installed" }
            $actual = (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $tmp $asset)).Hash.ToLowerInvariant()
            if ($actual -cne $expected) {
                throw "$asset did not match the published checksum in SHA256SUMS; nothing was installed"
            }

            $dir = if ($env:HIPPO_INSTALL_DIR) { $env:HIPPO_INSTALL_DIR } else { Join-Path $env:LOCALAPPDATA 'Programs\hippo' }
            try {
                $dir = (New-Item -ItemType Directory -Force -Path $dir).FullName
            } catch {
                throw "cannot create $dir; set HIPPO_INSTALL_DIR to a directory you can write to"
            }
            $exe = Join-Path $dir 'hippo.exe'
            $old = "$exe.old"

            $stage = Join-Path $tmp 'stage'
            Expand-Archive -LiteralPath (Join-Path $tmp $asset) -DestinationPath $stage
            $staged = Join-Path $stage 'hippo.exe'
            if (-not (Test-Path -LiteralPath $staged -PathType Leaf)) { throw "$asset holds no hippo.exe; nothing was installed" }
            # Run before it replaces anything, so a binary that cannot start here leaves the installed one in place.
            try {
                $installed = & $staged --version
            } catch {
                $installed = $null
            }
            if (-not $installed -or $LASTEXITCODE -ne 0) {
                throw 'the downloaded hippo did not run on this system; nothing was installed'
            }

            # Windows will not overwrite a running exe, but it will rename one, so a hippo still running is moved aside
            # to hippo.exe.old. A later run deletes it once nothing has it open.
            Remove-Item -LiteralPath $old -Force -ErrorAction SilentlyContinue
            if ((Test-Path -LiteralPath $exe) -and (Test-Locked $exe)) {
                try {
                    Move-Item -LiteralPath $exe -Destination $old
                } catch {
                    throw "$exe is in use and could not be moved aside; close hippo and rerun this script"
                }
            }
            try {
                Move-Item -LiteralPath $staged -Destination $exe -Force
            } catch {
                throw "cannot write to $dir; set HIPPO_INSTALL_DIR to a directory you can write to"
            }
            Write-Host "Installed hippo $installed to $exe"
        } finally {
            Remove-Item -LiteralPath $tmp -Recurse -Force -ErrorAction SilentlyContinue
        }

        # The user PATH is where new terminals look; this session gets the directory too, so hippo runs here at once.
        Add-UserPath $dir
        if (-not (Test-PathEntry $env:Path $dir)) {
            $env:Path = "$($env:Path.TrimEnd(';'));$dir"
        }

        # A hippo installed as a .NET tool, hippo.cmd in ~\.dotnet\tools, is the likely one.
        $found = Get-Command hippo -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($found -and $found.Source -ne $exe) {
            Write-Warning "another hippo is on your PATH at $($found.Source) and runs first. If it is the .NET tool's hippo.cmd, remove it with: dotnet tool uninstall -g hippo"
        }

        Write-Host 'Rerun this script to update.'
    }

    # Returns the version in the tag that releases/latest redirects to, or nothing.
    function Get-LatestVersion($url) {
        $request = @{ Uri = $url; Method = 'Head'; MaximumRedirection = 0; UserAgent = 'hippo-install'; UseBasicParsing = $true }
        # pwsh 7 treats the redirect as an error unless told not to; Windows PowerShell 5.1 has no such switch, and
        # either returns the response with an error or throws one carrying it.
        if ($PSVersionTable.PSVersion.Major -ge 7) { $request.SkipHttpErrorCheck = $true }
        $location = $null
        try {
            $location = (Invoke-WebRequest @request -ErrorAction SilentlyContinue).Headers.Location
        } catch {
            $headers = $_.Exception.Response.Headers
            if ($headers) {
                $location = $headers.Location
                if (-not $location) { $location = $headers['Location'] }
            }
        }
        # A string from 5.1, an array of one from pwsh 7, or a Uri from an exception.
        $location = [string]($location | Select-Object -First 1)
        if ($location -match '/tag/v([^/]+)$') { $Matches[1] }
    }

    # Returns the HTTP status, so a missing asset can be told from a failed connection.
    function Save-Download($url, $path) {
        try {
            Invoke-WebRequest -Uri $url -OutFile $path -UserAgent 'hippo-install' -UseBasicParsing
            200
        } catch {
            $response = $_.Exception.Response
            if (-not $response) { throw "could not download ${url}: $($_.Exception.Message)" }
            [int]$response.StatusCode
        }
    }

    # Whether another process has the file open, as Windows holds a running exe. Only a sharing violation counts: a file
    # that cannot be opened for want of permission is left to the move into place, which says the directory is not
    # writable.
    function Test-Locked($path) {
        try {
            [IO.File]::Open($path, 'Open', 'ReadWrite', 'None').Close()
            $false
        } catch {
            $e = $_.Exception
            if ($e.InnerException) { $e = $e.InnerException }
            # ERROR_SHARING_VIOLATION or ERROR_LOCK_VIOLATION, in the low word of the HRESULT.
            $e -is [IO.IOException] -and (($e.HResult -band 0xFFFF) -in 32, 33)
        }
    }

    # Adds the directory to the user PATH in the registry. The value is read and written as stored, so entries such as
    # %USERPROFILE%\AppData\Local\Microsoft\WindowsApps keep their variables and the value keeps its kind, REG_EXPAND_SZ
    # on a new profile; [Environment]'s methods would expand every entry and store REG_SZ.
    function Add-UserPath($dir) {
        $key = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey('Environment')
        try {
            $userPath = $key.GetValue('Path', '', 'DoNotExpandEnvironmentNames')
            if (Test-PathEntry $userPath $dir) { return }
            $kind = if ($key.GetValueNames() -contains 'Path') { $key.GetValueKind('Path') } else { 'ExpandString' }
            $newPath = if ($userPath) { "$($userPath.TrimEnd(';'));$dir" } else { $dir }
            $key.SetValue('Path', $newPath, $kind)
        } finally {
            $key.Dispose()
        }
        # Removing a user variable through [Environment] tells running programs, Explorer among them, that the
        # environment changed, so terminals started from them see the new PATH. This variable never exists, so nothing
        # else changes.
        [Environment]::SetEnvironmentVariable('HIPPO_INSTALL_REFRESH', $null, 'User')
        Write-Host "Added $dir to your user PATH. Terminals that were already open need reopening to find hippo."
    }

    # Whether the PATH-style list holds the directory, as written or once its variables are expanded.
    function Test-PathEntry($pathList, $dir) {
        $dir = $dir.TrimEnd('\')
        foreach ($entry in "$pathList" -split ';') {
            if ([Environment]::ExpandEnvironmentVariables($entry).TrimEnd('\') -eq $dir) { return $true }
        }
        $false
    }

    try {
        Install-Hippo
    } catch {
        [Console]::Error.WriteLine("hippo-install: $($_.Exception.Message)")
        # Run as a file, the exit code tells the caller it failed. Under `irm | iex` there is no file, and exit would
        # close the user's terminal.
        if ($PSCommandPath) { exit 1 }
    }
}
