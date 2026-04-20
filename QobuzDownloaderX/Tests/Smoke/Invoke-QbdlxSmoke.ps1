[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Email,

    [Parameter(Mandatory = $true)]
    [string]$Password
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$binRoot = Join-Path $repoRoot "bin\Release"
$exePath = Join-Path $binRoot "QobuzDownloaderX.exe"
$qopenApiPath = Join-Path $binRoot "Qo(penAPI).dll"

if (-not (Test-Path $exePath)) {
    throw "Missing executable: $exePath"
}

if (-not (Test-Path $qopenApiPath)) {
    throw "Missing QopenAPI DLL: $qopenApiPath"
}

[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12 -bor 12288
[Reflection.Assembly]::LoadFrom($qopenApiPath) | Out-Null

function Get-StreamSummary {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Service,

        [Parameter(Mandatory = $true)]
        [string]$AppId,

        [Parameter(Mandatory = $true)]
        [string]$UserAuthToken,

        [Parameter(Mandatory = $true)]
        [string]$AppSecret,

        [Parameter(Mandatory = $true)]
        [string]$TrackId,

        [Parameter(Mandatory = $true)]
        [string]$FormatId
    )

    $stream = $Service.TrackGetFileUrl($TrackId, $FormatId, $AppId, $UserAuthToken, $AppSecret)
    if (-not $stream -or [string]::IsNullOrWhiteSpace($stream.StreamURL)) {
        return [pscustomobject]@{
            Requested  = $FormatId
            Returned   = $null
            MimeType   = $null
            BitDepth   = $null
            SampleRate = $null
            Signature  = $null
        }
    }

    $request = [Net.HttpWebRequest]::Create($stream.StreamURL)
    $request.Method = "GET"
    $request.AddRange(0, 3)
    $request.Timeout = 30000
    $response = $request.GetResponse()
    try {
        $buffer = New-Object byte[] 4
        $read = $response.GetResponseStream().Read($buffer, 0, 4)
        $signature = [BitConverter]::ToString($buffer, 0, $read)
    }
    finally {
        $response.Close()
    }

    [pscustomobject]@{
        Requested  = $FormatId
        Returned   = [string]$stream.FormatID
        MimeType   = [string]$stream.MimeType
        BitDepth   = [string]$stream.BitDepth
        SampleRate = [string]$stream.SampleRate
        Signature  = $signature
    }
}

$startupProcess = Start-Process -FilePath $exePath -WorkingDirectory $binRoot -PassThru -WindowStyle Hidden
Start-Sleep -Seconds 10
$startupPassed = -not $startupProcess.HasExited
if ($startupPassed) {
    Stop-Process -Id $startupProcess.Id -Force
}

$oldConsole = [Console]::Out
[Console]::SetOut([IO.TextWriter]::Null)
try {
    $service = New-Object QopenAPI.Service
    $appId = $service.GetAppID().App_ID
    $user = $service.Login($appId, $Email, $Password, $null)
    $appSecret = $service.GetAppSecret($appId, $user.UserAuthToken).App_Secret
}
finally {
    [Console]::SetOut($oldConsole)
}

$sourceFiles = @{
    DownloadAlbum = Join-Path $repoRoot "Helpers\Download\DownloadAlbum.cs"
    DownloadTrack = Join-Path $repoRoot "Helpers\Download\DownloadTrack.cs"
    DownloadFile = Join-Path $repoRoot "Helpers\Download\DownloadFile.cs"
    Misc = Join-Path $repoRoot "Helpers\Miscellaneous.cs"
    Search = Join-Path $repoRoot "Helpers\SearchPanelHelper.cs"
    Rename = Join-Path $repoRoot "Helpers\RenameTemplates.cs"
    GetInfo = Join-Path $repoRoot "Helpers\GetInfo.cs"
    Padding = Join-Path $repoRoot "Helpers\PaddingNumbers.cs"
}

$source = @{}
foreach ($entry in $sourceFiles.GetEnumerator()) {
    $source[$entry.Key] = Get-Content $entry.Value -Raw
}

$sourceChecks = [ordered]@{
    PaddingFixed = $source.DownloadAlbum.Contains("paddedDiscLength = padNumber.padDiscs(QoAlbum);")
    PaddingSafeForZero = $source.Padding.Contains("return GetPaddingLength(QoPlaylist?.TracksCount ?? 0);") -and $source.Padding.Contains("return GetPaddingLength(QoAlbum?.MediaCount ?? 0);")
    PlaylistMergeFixed = $source.Misc.Contains("f.QoItem = playlistTrackGetInfo.QoItem ?? item;")
    AsyncArtworkFixed = $source.Search.Contains("artwork.LoadAsync(imageUrl);")
    PlaylistQualityFixed = $source.Rename.Contains("template = RenameFormatTemplate(") -and $source.Rename.Contains("%formatwithquality%")
    PlaylistTemplateGuard = $source.Rename.Contains("isPlaylistDirectoryTemplate")
    TimeoutTokenGuard = $source.GetInfo.Contains("public CancellationToken UiCancellationToken") -and $source.Misc.Contains("TryCancelUiUpdates")
    CompletionGuards = $source.Misc.Contains("if (albumSucceeded)") -and $source.Misc.Contains("if (trackSucceeded)") -and $source.Misc.Contains("if (playlistSucceeded)") -and $source.Misc.Contains("if (artistSucceeded)") -and $source.Misc.Contains("if (labelSucceeded)") -and $source.Misc.Contains("if (userOperationSucceeded)")
    BoolReturnFlow = $source.DownloadAlbum.Contains("internal async Task<bool> DownloadAlbumAsync") -and $source.DownloadAlbum.Contains("internal async Task<bool> DownloadTracksAsync") -and $source.DownloadTrack.Contains("public async Task<bool> DownloadTrackAsync") -and $source.DownloadTrack.Contains("public async Task<bool> DownloadPlaylistTrackAsync") -and $source.DownloadFile.Contains("public async Task<bool> DownloadStream(")
}

$sourceChecks.Passed = ($sourceChecks.GetEnumerator() | Where-Object { $_.Value -is [bool] } | Where-Object { -not $_.Value }).Count -eq 0

$result = [ordered]@{
    Startup = [pscustomobject]@{
        Passed = $startupPassed
        ProcessId = $startupProcess.Id
    }
    Login = [pscustomobject]@{
        Passed = [bool]$user
        DisplayName = $user.UserInfo.DisplayName
        Subscription = $user.UserInfo.Credential.Label
        HiRes = [bool]$user.UserInfo.Credential.Parameters.HiResStreaming
    }
    Streams = [pscustomobject]@{
        Track13176083 = @(
            Get-StreamSummary -Service $service -AppId $appId -UserAuthToken $user.UserAuthToken -AppSecret $appSecret -TrackId "13176083" -FormatId "27"
            Get-StreamSummary -Service $service -AppId $appId -UserAuthToken $user.UserAuthToken -AppSecret $appSecret -TrackId "13176083" -FormatId "7"
            Get-StreamSummary -Service $service -AppId $appId -UserAuthToken $user.UserAuthToken -AppSecret $appSecret -TrackId "13176083" -FormatId "6"
            Get-StreamSummary -Service $service -AppId $appId -UserAuthToken $user.UserAuthToken -AppSecret $appSecret -TrackId "13176083" -FormatId "5"
        )
        Track65115165 = @(
            Get-StreamSummary -Service $service -AppId $appId -UserAuthToken $user.UserAuthToken -AppSecret $appSecret -TrackId "65115165" -FormatId "27"
            Get-StreamSummary -Service $service -AppId $appId -UserAuthToken $user.UserAuthToken -AppSecret $appSecret -TrackId "65115165" -FormatId "6"
            Get-StreamSummary -Service $service -AppId $appId -UserAuthToken $user.UserAuthToken -AppSecret $appSecret -TrackId "65115165" -FormatId "5"
        )
    }
    SourceChecks = $sourceChecks
}

$result | ConvertTo-Json -Compress -Depth 6
