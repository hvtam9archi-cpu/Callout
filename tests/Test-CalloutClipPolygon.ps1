$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$sources = @(
    (Join-Path $repositoryRoot 'Callout\CalloutClipPolygon.cs'),
    (Join-Path $repositoryRoot 'Callout\CalloutLeaderRouter.cs'),
    (Join-Path $PSScriptRoot 'CalloutClipPolygonTests.cs'),
    (Join-Path $PSScriptRoot 'CalloutLeaderRouterTests.cs')
)
Add-Type -Path $sources
[Callout.Tests.CalloutClipPolygonTests]::Run()
[Callout.Tests.CalloutLeaderRouterTests]::Run()
