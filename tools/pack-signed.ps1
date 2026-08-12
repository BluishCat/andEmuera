<#
.SYNOPSIS
    暗号化して置いたパスワードを使って pack.ps1 を走らせる。

.DESCRIPTION
    pack.ps1 は鍵のパスワードを環境変数 ANDEMUERA_KEYSTORE_PASS から取る
    (正確には、変数名のまま MSBuild へ渡すのでスクリプトは中身を読まない)。
    毎回手で環境変数に入れるかわりに、save-keystore-pass.ps1 で置いた
    暗号化ファイルから復号し、このプロセスの環境変数にだけ入れて pack.ps1 を呼ぶ。

    平文はこのプロセスの中だけに存在する。永続的な環境変数にはしないので、
    他のプロセスからは見えない。終わったら環境変数から消す。

    引数はそのまま pack.ps1 へ渡る。

.EXAMPLE
    .\tools\pack-signed.ps1
    .\tools\pack-signed.ps1 -SkipSource
#>
[CmdletBinding()]
param(
    # 暗号化したパスワードの置き場
    [string]$PassFile = (Join-Path $env:USERPROFILE '.andemuera\pass.dpapi'),

    # ここから下は pack.ps1 と同じもの。渡されたものだけ転送する。
    # 配列のスプラットは位置引数として渡ってしまうので、名前付きで渡すために自分でも宣言する
    [string]$Version,
    [string]$KeyStore,
    [string]$KeyAlias,
    [switch]$SkipSource,
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'

# 明示的に渡されたものだけを pack.ps1 へ。既定値は pack.ps1 側のものを使わせる
$packParams = @{}
foreach ($name in $PSBoundParameters.Keys) {
    if ($name -ne 'PassFile') { $packParams[$name] = $PSBoundParameters[$name] }
}

$passVar = 'ANDEMUERA_KEYSTORE_PASS'
$pack = Join-Path $PSScriptRoot 'pack.ps1'

if (-not (Test-Path $pack)) { throw "pack.ps1 が見つかりません: $pack" }

if (-not (Test-Path $PassFile)) {
    throw @"
暗号化したパスワードがありません: $PassFile
先に置いてください:
    .\tools\save-keystore-pass.ps1

環境変数に直接入れて pack.ps1 を叩くやり方もそのまま使えます。
"@
}

if (Get-Item "env:$passVar" -ErrorAction SilentlyContinue) {
    Write-Host "環境変数 $passVar がすでにあるので、そちらを使います。" -ForegroundColor Yellow
    & $pack @packParams
    return
}

$bstr = [IntPtr]::Zero
try {
    # 鍵を渡さない ConvertTo-SecureString は DPAPI として復号する。
    # 別ユーザ・別マシンのファイルならここで失敗する
    $secure = (Get-Content $PassFile -Raw).Trim() | ConvertTo-SecureString
    $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
    Set-Item "env:$passVar" ([Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr))

    & $pack @packParams
}
finally {
    if ($bstr -ne [IntPtr]::Zero) { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
    Remove-Item "env:$passVar" -ErrorAction SilentlyContinue
}
