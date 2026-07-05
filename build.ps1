
param(
    [string]$Version = "",
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$RemainingArgs = @()
)

if ($Version -eq "--version") {
    if ($RemainingArgs.Count -eq 0) {
        throw "--version requires a value."
    }
    $Version = $RemainingArgs[0]
} elseif ($Version.StartsWith("--version=", [System.StringComparison]::OrdinalIgnoreCase)) {
    $Version = $Version.Substring("--version=".Length)
}

$gatedArtifactPrefix = "gated"
if (-not [string]::IsNullOrWhiteSpace($Version)) {
    $safeVersion = ($Version.Trim() -replace '[^0-9A-Za-z]+', '-').Trim('-')
    if ($safeVersion.Length -gt 0) {
        $gatedArtifactPrefix = "gated-$safeVersion"
    }
}

rm -r ./artifacts
mkdir ./artifacts

# UNIX x64

dotnet build gated.csproj -c Release -r linux-x64
mv ./bin/Release/net10.0/linux-x64 ./artifacts/gated-posix-x64
rm -r ./bin/Release

dotnet publish ./Updater/update.csproj -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true -p:PublishDir="./bin/linux-x64"
mkdir ./artifacts/updater-posix-x64
mv ./Updater/bin/linux-x64/update ./artifacts/updater-posix-x64/update
rm -r ./Updater/obj
rm -r ./Updater/bin

cd ./artifacts/gated-posix-x64
zip -r "../$gatedArtifactPrefix-posix-x64.zip" .
cd ../updater-posix-x64
zip -r ../updater-posix-x64.zip .

cd ../..
rm -r ./artifacts/gated-posix-x64
rm -r ./artifacts/updater-posix-x64

# Windows x64

dotnet build gated.csproj -c Release -r win-x64
mv ./bin/Release/net10.0/win-x64 ./artifacts/gated-win-x64
rm -r ./bin/Release

dotnet publish ./Updater/update.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:PublishDir="./bin/win-x64"
mkdir ./artifacts/updater-win-x64
mv ./Updater/bin/win-x64/update.exe ./artifacts/updater-win-x64/update.exe
rm -r ./Updater/obj
rm -r ./Updater/bin

cd ./artifacts/gated-win-x64
zip -r "../$gatedArtifactPrefix-win-x64.zip" .
cd ../updater-win-x64
zip -r ../updater-win-x64.zip .

cd ../..
rm -r ./artifacts/updater-win-x64

# macOS Intel x64

dotnet build gated.csproj -c Release -r osx-x64
mv ./bin/Release/net10.0/osx-x64 ./artifacts/gated-osx-x64
rm -r ./bin/Release

dotnet publish ./Updater/update.csproj -c Release -r osx-x64 --self-contained true -p:PublishSingleFile=true -p:PublishDir="./bin/osx-x64"
mkdir ./artifacts/updater-osx-x64
mv ./Updater/bin/osx-x64/update ./artifacts/updater-osx-x64/update
rm -r ./Updater/obj
rm -r ./Updater/bin

cd ./artifacts/gated-osx-x64
zip -r "../$gatedArtifactPrefix-osx-x64.zip" .
cd ../updater-osx-x64
zip -r ../updater-osx-x64.zip .

cd ../..
rm -r ./artifacts/gated-osx-x64
rm -r ./artifacts/updater-osx-x64

# macOS ARM64

dotnet build gated.csproj -c Release -r osx-arm64
mv ./bin/Release/net10.0/osx-arm64 ./artifacts/gated-osx-arm64
rm -r ./bin/Release

dotnet publish ./Updater/update.csproj -c Release -r osx-arm64 --self-contained true -p:PublishSingleFile=true -p:PublishDir="./bin/osx-arm64"
mkdir ./artifacts/updater-osx-arm64
mv ./Updater/bin/osx-arm64/update ./artifacts/updater-osx-arm64/update
rm -r ./Updater/obj
rm -r ./Updater/bin

cd ./artifacts/gated-osx-arm64
zip -r "../$gatedArtifactPrefix-osx-arm64.zip" .
cd ../updater-osx-arm64
zip -r ../updater-osx-arm64.zip .

cd ../..
rm -r ./artifacts/gated-osx-arm64
rm -r ./artifacts/updater-osx-arm64

# build msi

dotnet build ./Installer/Installer.wixproj
rm -r ./Installer/bin
rm -r ./Installer/obj
rm "./artifacts/$gatedArtifactPrefix-win-x64.msi"
mv ./artifacts/Installer.msi "./artifacts/$gatedArtifactPrefix-win-x64.msi"
rm ./artifacts/Installer*
