param([switch]$Tests,[string]$OutputDirectory="bin")
$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    $compiler = "$env:WINDIR/Microsoft.NET/Framework64/v4.0.30319/csc.exe"
    $wpf = Join-Path (Split-Path $compiler) WPF
    $refs = foreach ($name in 'UIAutomationClient','UIAutomationTypes','WindowsBase','PresentationCore','PresentationFramework') {
        "/reference:$wpf/$name.dll"
    }
    $refs += '/reference:System.Xaml.dll','/reference:System.Drawing.dll','/reference:System.Windows.Forms.dll','/reference:System.Web.Extensions.dll'
    $resources = @(Get-ChildItem UI/*.xaml | ForEach-Object { "/resource:$($_.FullName),$($_.Name)" })
    if (Test-Path UI/Textures) {
        $resources += Get-ChildItem UI/Textures/* -File | ForEach-Object { "/resource:$($_.FullName),$($_.Name)" }
    }
    $core = Get-ChildItem Wechat/*.cs | ForEach-Object FullName
    $app = Get-ChildItem *.cs | ForEach-Object FullName
    New-Item -ItemType Directory -Force $OutputDirectory | Out-Null
    & $compiler /nologo /codepage:65001 /langversion:5 /optimize+ /platform:x64 /target:winexe /win32icon:UI/tray.ico "/out:$OutputDirectory/NudgeWatch.exe" @refs @resources @app @core
    if ($LASTEXITCODE) { throw '应用编译失败' }
    if ($Tests) {
        foreach ($name in 'PrototypeTests','Acceptance') {
            [string[]]$fixtures = if ($name -eq 'Acceptance') { "$PSScriptRoot\Tests\WeChatFixture.cs" }
            & $compiler /nologo /codepage:65001 /langversion:5 /optimize+ /platform:x64 /target:exe "/out:$OutputDirectory/$name.exe" @refs "/reference:$OutputDirectory/NudgeWatch.exe" "$PSScriptRoot\Tests\$name.cs" @fixtures
            if ($LASTEXITCODE) { throw "$name 编译失败" }
        }
    }
} finally { Pop-Location }




