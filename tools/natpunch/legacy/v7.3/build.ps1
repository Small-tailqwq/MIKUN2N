$ErrorActionPreference = "Stop"

$mingwBin = if ($env:N3N_TOOLCHAIN_BIN) { $env:N3N_TOOLCHAIN_BIN } else { "$env:USERPROFILE\mingw64\mingw64\bin" }
if (Test-Path -LiteralPath $mingwBin) {
    $env:Path = "$mingwBin;$env:Path"
}

& gcc -std=gnu17 -O2 -Wall -Wextra -Werror `
    -o natpunch-v7.3.exe natpunch.c -lws2_32
if ($LASTEXITCODE -ne 0) {
    throw "natpunch v7.3 编译失败"
}

& .\natpunch-v7.3.exe --self-test
if ($LASTEXITCODE -ne 0) {
    throw "natpunch v7.3 模型自测失败"
}
