
# UNIX x64

dotnet build gated.csproj -c Release -r linux-x64
rm -r ./artifacts/gated-posix-x64 || true
rm ./artifacts/gated-posix-x64.zip || true
mv ./bin/Release/net10.0/linux-x64 ./artifacts/gated-posix-x64
rm -r ./bin/Release

dotnet publish ./Updater/update.csproj -c Release -r linux-x64 \
  --self-contained true -p:PublishSingleFile=true -p:PublishDir="./bin/linux-x64"
rm -r ./artifacts/updater-posix-x64 || true
rm ./artifacts/updater-posix-x64.zip || true
mkdir ./artifacts/updater-posix-x64
mv ./Updater/bin/linux-x64/update ./artifacts/updater-posix-x64/update
rm -r ./Updater/obj
rm -r ./Updater/bin

cd ./artifacts/gated-posix-x64
zip -r ../gated-posix-x64.zip .
cd ../updater-posix-x64
zip -r ../updater-posix-x64.zip .

cd ../..
rm -r ./artifacts/gated-posix-x64
rm -r ./artifacts/updater-posix-x64

# Windows x64

dotnet build gated.csproj -c Release -r win-x64
rm -r ./artifacts/gated-win-x64 || true
rm ./artifacts/gated-win-x64.zip || true
mv ./bin/Release/net10.0/win-x64 ./artifacts/gated-win-x64
rm -r ./bin/Release

dotnet publish ./Updater/update.csproj -c Release -r win-x64 \
  --self-contained true -p:PublishSingleFile=true -p:PublishDir="./bin/win-x64"
rm -r ./artifacts/updater-win-x64 || true
rm ./artifacts/updater-win-x64.zip || true
mkdir ./artifacts/updater-win-x64
mv ./Updater/bin/win-x64/update.exe ./artifacts/updater-win-x64/update.exe
rm -r ./Updater/obj
rm -r ./Updater/bin

cd ./artifacts/gated-win-x64
zip -r ../gated-win-x64.zip .
cd ../updater-win-x64
zip -r ../updater-win-x64.zip .

cd ../..
rm -r ./artifacts/gated-win-x64
rm -r ./artifacts/updater-win-x64

