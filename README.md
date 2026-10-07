# CooApp (Coo) — Baby Log & Sleep

Coo is an offline-first, private baby care logging app built for Android. It allows parents and their invited household members to log feeds (breast/bottle), diaper changes, sleeps, and pumping sessions.

The app is built with privacy and speed in mind: it uses a self-hosted backend, local SQLite storage for offline-first sync, and has no Firebase integration.

## Tech Stack
* **Mobile Client**: .NET MAUI (C# 13, .NET 10.0), exclusively targeting Android 16 (API 36).
* **Local Storage**: EF Core with SQLite.
* **Backend**: ASP.NET Core API (.NET 10.0) with EF Core and PostgreSQL.
* **Hosting**: Self-hosted on a Hetzner VPS (`coldstart-prod`) behind Caddy (HTTPS reverse proxy). Deployed via Docker Compose.
* **Design System**: Washi UX (custom tokens, built-in dark/light modes, tabular figures with Figtree).

## Core Concepts
* **Offline-First Sync**: Uses a Hybrid Logical Clock (HLC) and last-writer-wins conflict resolution per record. Real-time updates use a SignalR doorbell.
* **Extensible Domain Model**: All entries use a single `CareEntry` entity, with specific details stored as JSON to allow adding new entry kinds without database migrations.
* **Identity & Households**: Google Credential Manager authentication (no passwords). Users are grouped into Households, and entries belong to a household.

## Building and Testing Locally

### Backend & Tests
The global .NET SDK is used for backend execution and running tests.
```sh
dotnet test Coo.Tests.slnf                                   # Run all tests
dotnet format Coo.Tests.slnf --verify-no-changes             # Formatting gate
dotnet run --project src/Backend/Coo.Api --urls http://localhost:5310
```

### Android App
The `maui-android` workload is installed in a user-local SDK to avoid polluting the global environment.
```sh
export DOTNET_ROOT=$HOME/dotnet-maui
export PATH=$HOME/dotnet-maui:$PATH
export JAVA_HOME=$HOME/Library/Java/JavaVirtualMachines/jdk-17.0.20+8/Contents/Home

# Build a Debug APK
dotnet build src/Mobile/Coo.Mobile/Coo.Mobile.csproj -c Debug -f net10.0-android --no-restore \
  -p:UseDefaultPublishRuntimeIdentifier=false -p:EmbedAssembliesIntoApk=true \
  -p:AndroidSdkDirectory=$HOME/android-sdk -p:JavaSdkDirectory=$JAVA_HOME
```

## Deployment
Deployment to the Hetzner host (`coldstart-prod`) is done via a deploy script that builds the image on the server and runs migrations automatically:
```sh
./deploy/deploy-prod.sh --confirm
```

For more detailed guides, refer to `BUILD.md` and `DEPLOY.md`. 
Architectural details and domain rules can be found in the `docs/` folder.
