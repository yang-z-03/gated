
# UNIX x64

dotnet build gated.csproj -c Release -r linux-x64
rm -r ./artifacts/gated-posix-x64
rm ./artifacts/gated-posix-x64.zip
mv ./bin/Release/net10.0/linux-x64 ./artifacts/gated-posix-x64
rm -r ./bin/Release

dotnet publish ./Updater/update.csproj -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true -p:PublishDir="./bin/linux-x64"
rm -r ./artifacts/updater-posix-x64
rm ./artifacts/updater-posix-x64.zip
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
rm -r ./artifacts/gated-win-x64
rm ./artifacts/gated-win-x64.zip
mv ./bin/Release/net10.0/win-x64 ./artifacts/gated-win-x64
rm -r ./bin/Release

dotnet publish ./Updater/update.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:PublishDir="./bin/win-x64"
rm -r ./artifacts/updater-win-x64
rm ./artifacts/updater-win-x64.zip
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

# macOS Intel x64

dotnet build gated.csproj -c Release -r osx-x64
rm -r ./artifacts/gated-osx-x64
rm ./artifacts/gated-osx-x64.zip
mv ./bin/Release/net10.0/osx-x64 ./artifacts/gated-osx-x64
rm -r ./bin/Release

dotnet publish ./Updater/update.csproj -c Release -r osx-x64 --self-contained true -p:PublishSingleFile=true -p:PublishDir="./bin/osx-x64"
rm -r ./artifacts/updater-osx-x64
rm ./artifacts/updater-osx-x64.zip
mkdir ./artifacts/updater-osx-x64
mv ./Updater/bin/osx-x64/update ./artifacts/updater-osx-x64/update
rm -r ./Updater/obj
rm -r ./Updater/bin

cd ./artifacts/gated-osx-x64
zip -r ../gated-osx-x64.zip .
cd ../updater-osx-x64
zip -r ../updater-osx-x64.zip .

cd ../..
rm -r ./artifacts/gated-osx-x64
rm -r ./artifacts/updater-osx-x64

