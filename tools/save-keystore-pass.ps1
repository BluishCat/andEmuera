<#
.SYNOPSIS
    リリース署名鍵のパスワードを暗号化して置く。最初の 1 回だけ実行する。

.DESCRIPTION
    pack.ps1 は鍵のパスワードを環境変数 ANDEMUERA_KEYSTORE_PASS から取る。
    毎回手で入れるかわりに、ここで DPAPI 暗号化して置いておき、
    pack-signed.ps1 に復号させる。

    DPAPI はユーザ + マシン単位なので、このファイルを他のマシンや他のユーザへ
    持ち出しても復号できない。逆に、OS の再インストールや別ユーザへの移行では
    読めなくなるので、鍵のパスワードそのものの控えは別に残しておくこと。

    置き場はリポジトリの外 (%USERPROFILE%\.andemuera\)。keystore と同じ場所。

.EXAMPLE
    .\tools\save-keystore-pass.ps1
#>
[CmdletBinding()]
param(
    # 暗号化したパスワードの置き場。リポジトリの外に置く
    [string]$PassFile = (Join-Path $env:USERPROFILE '.andemuera\pass.dpapi')
)

$ErrorActionPreference = 'Stop'

if (Test-Path $PassFile) {
    Write-Host "すでにあります: $PassFile" -ForegroundColor Yellow
    $answer = Read-Host '入れ直しますか? (y/N)'
    if ($answer -ne 'y') { exit 1 }
}

$dir = Split-Path -Parent $PassFile
if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }

Write-Host "make-keystore.ps1 で鍵を作ったときのパスワードを入れてください。" -ForegroundColor Cyan
Write-Host "入力は画面に出ません。"
Write-Host ""

$secure = Read-Host '鍵のパスワード' -AsSecureString
if ($secure.Length -eq 0) { throw 'パスワードが空です。' }

# ConvertFrom-SecureString に鍵を渡さないと DPAPI (ユーザ + マシン単位) になる。
# 出力は 16 進文字列なので ascii で保存してよい
$secure | ConvertFrom-SecureString | Set-Content -Path $PassFile -Encoding ascii

Write-Host ""
Write-Host "置きました: $PassFile" -ForegroundColor Green
Write-Host ""
Write-Host "以降はこれで署名込みの配布物が作れます:" -ForegroundColor Cyan
Write-Host "  .\tools\pack-signed.ps1"
