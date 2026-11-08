param(
    [Parameter(Mandatory = $true)][string]$CoreConsolePath,
    [Parameter(Mandatory = $true)][string]$TemplatePath
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$assembly = Join-Path $PSScriptRoot 'AutoCAD\bin\x64\Release\net48\Callout.NativeTests.dll'
$plugin = Join-Path $repositoryRoot 'Callout\bin\x64\Release\Callout.dll'
if (!(Test-Path -LiteralPath $assembly)) { throw 'Build Callout.NativeTests.csproj first.' }
$testRoot = Join-Path $env:TEMP ('CalloutNative-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot | Out-Null
$script = Join-Path $testRoot 'run.scr'
$lines = @(
    '(setq ctTestSecureLoad (getvar "SECURELOAD"))',
    '(setvar "SECURELOAD" 0)',
    '_.NETLOAD',
    ('"' + $assembly.Replace('\', '/') + '"'),
    '(setvar "SECURELOAD" ctTestSecureLoad)',
    'CTNATIVEREGRESSION',
    'CTLEADERSETUP',
    'CTLEADEREDIT',
    'CTLEADERCHECKEDIT',
    '_.U',
    '_.REDO',
    'CTLEADERCHECKEDIT',
    '_.U',
    'CTLEADERCHECKUNDO',
    'CTLEADERFINISH',
    '_.QUIT',
    '_N',
    ''
)
Set-Content -LiteralPath $script -Value $lines -Encoding ascii
$previousOutput = $env:CALLOUT_TEST_OUTPUT
$previousHash = $env:CALLOUT_EXPECTED_HASH
try {
    $env:CALLOUT_TEST_OUTPUT = $testRoot
    $env:CALLOUT_EXPECTED_HASH = (Get-FileHash -LiteralPath $plugin -Algorithm SHA256).Hash
    $arguments = @('/i', ('"' + $TemplatePath + '"'), '/s', ('"' + $script + '"'),
        '/l', 'en-US', '/isolate', 'CalloutRegression', ('"' + (Join-Path $testRoot 'profile') + '"'), '/readonly')
    $process = Start-Process -FilePath $CoreConsolePath -ArgumentList $arguments -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput (Join-Path $testRoot 'console.log') -RedirectStandardError (Join-Path $testRoot 'console.err')
    Write-Output "AutoCAD test logs: $testRoot"
    if (!$process.WaitForExit(45000)) {
        Stop-Process -Id $process.Id
        throw "Test-only Core Console timed out. See $testRoot"
    }
    $result = Join-Path $testRoot 'result.txt'
    if (!(Test-Path -LiteralPath $result)) { throw "Core Console did not run the tests. See $testRoot" }
    $text = Get-Content -LiteralPath $result -Raw
    Write-Output $text
    if (!$text.StartsWith('PASS:')) { throw "Native crop regression failed. See $result" }
    $leaderResult = Join-Path $testRoot 'leader-commands.txt'
    if (!(Test-Path -LiteralPath $leaderResult)) { throw 'Native leader command tests did not run.' }
    $leaderText = Get-Content -LiteralPath $leaderResult -Raw
    Write-Output $leaderText
    if (!$leaderText.StartsWith('PASS:')) { throw "Native leader regression failed. See $leaderResult" }
}
finally {
    $env:CALLOUT_TEST_OUTPUT = $previousOutput
    $env:CALLOUT_EXPECTED_HASH = $previousHash
}
