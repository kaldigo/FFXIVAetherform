$ErrorActionPreference = 'Stop'
$cache = Join-Path $PSScriptRoot '../out/solver'
$archive = Join-Path $cache 'Proteus.zip'
$expected = '4148791D2107EFE17707F4284718376CE87BD0C656C1F7542239B1DE1F27DED8'
New-Item -ItemType Directory -Force $cache | Out-Null
if (!(Test-Path -LiteralPath $archive)) {
    Invoke-WebRequest 'https://github.com/kaldigo/proteus/releases/download/v2611.1659.0.6/Proteus.zip' -OutFile $archive
}
if ((Get-FileHash -LiteralPath $archive).Hash -ne $expected) { throw 'Headless refit library checksum mismatch.' }
Expand-Archive -LiteralPath $archive -DestinationPath (Join-Path $cache 'lib') -Force
