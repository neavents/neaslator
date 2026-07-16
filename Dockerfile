FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY neaslator/Directory.Packages.props neaslator/
COPY neaslator/Directory.Build.props neaslator/
COPY neaslator/nuget.config neaslator/

COPY neavents-messaging-contracts/src/Neavents.Messaging.Contracts/Neavents.Messaging.Contracts.csproj neavents-messaging-contracts/src/Neavents.Messaging.Contracts/
COPY neaslator/src/Neaslator/Neaslator.csproj neaslator/src/Neaslator/

RUN dotnet restore neaslator/src/Neaslator/Neaslator.csproj

COPY neavents-messaging-contracts/src/Neavents.Messaging.Contracts/ neavents-messaging-contracts/src/Neavents.Messaging.Contracts/
COPY neaslator/src/Neaslator/ neaslator/src/Neaslator/

RUN dotnet publish neaslator/src/Neaslator/Neaslator.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
EXPOSE 5300
ENTRYPOINT ["dotnet", "Neaslator.dll"]
