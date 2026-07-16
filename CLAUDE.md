# Neaslator: Step-by-Step Implementation Runbook

> **For:** Claude Code / DeepSeek V4 Pro / any AI coding agent operating inside the Neavents monorepo.
> **Rule:** Execute phases in strict order. Do not skip. Do not start Phase N+1 until Phase N's verification passes.
> **Runtime:** .NET 10 | **License:** MIT (open source)
> **Cache:** Garnet (not Redis) — wire-compatible with Redis protocol, use StackExchange.Redis client.

---

## What Is Neaslator

An asynchronous, multi-tenant, LLM-agnostic translation microservice. It translates restaurant menu text (section names, item names, item descriptions) into up to 75 target languages.

**Core pipeline:**
```
MenuPublishedEvent IN
  → Debounce (5s coalescing window)
  → Diff Engine (extract only changed text)
  → Normalize text (Unicode NFC, strip invisible chars, collapse whitespace)
  → Hash (XxHash3 64-bit)
  → Cache lookup (L1 Garnet → L2 PostgreSQL)
  → LLM translation (cache misses only, batched by section)
  → Store translations (L2 PostgreSQL + L1 Garnet)
  → Publish TranslationCompletedEvent OUT
```

**Neaslator does NOT write to Cloudflare KV.** It publishes a `TranslationCompletedEvent` with the completed translation data. Downstream consumers (your edge API, webhooks, databases, whatever) subscribe to that event and do what they need. This makes Neaslator a generic, reusable translation engine.

---

## Pre-Implementation: Codebase Discovery

**Before writing any code, read these. The information you gather here is used in every phase.**

### Discovery Task 1: Menu Service Domain

```
Location: find the menu service project (likely neavents-menu-service/)
Read: the domain entities for Menu, Section, MenuItem
```

Record these facts — you will reference them later:
- [ ] Entity class names (e.g., `Menu`, `MenuSection`, `MenuItem` or different?)
- [ ] ID types (Guid? Ulid? int?)
- [ ] The property name for item display name (e.g., `Name`, `Title`, `DisplayName`?)
- [ ] The property name for item description (e.g., `Description`, `Details`?)
- [ ] The property name for section name
- [ ] Does Menu have a `DefaultLanguage` or `SourceLanguage` property?
- [ ] Does Menu have `VenueType` and/or `CuisineType` properties? If not, does the Venue entity?
- [ ] What is the relationship structure? Menu → has many Sections → has many Items?
- [ ] Is there any existing publish/draft state tracking?

### Discovery Task 2: Messaging Contracts

```
Location: find the shared contracts project (likely neavents-messaging-contracts/)
Read: all existing event/command contracts
```

Record:
- [ ] Namespace convention (e.g., `Neavents.Contracts.Events`, `Neavents.Messaging.Contracts`?)
- [ ] Naming convention (e.g., `MenuPublishedEvent`, `MenuPublishedIntegrationEvent`?)
- [ ] Does a `MenuPublishedEvent` already exist? If yes, what properties does it carry?
- [ ] Property type conventions (DateTimeOffset vs DateTime? Guid vs string?)
- [ ] Is this a NuGet package or a project reference?
- [ ] How are contracts organized? By service? By type? Flat?

### Discovery Task 3: Edge API

```
Location: find the edge API project (likely neavents-qrmenu-edge-api/)
Read: the Cloudflare KV write logic and JSON structure
```

Record:
- [ ] KV key format (e.g., `menu:{menuId}`, `venue:{venueId}:menu:{menuId}`?)
- [ ] The exact JSON structure written to KV for a menu
- [ ] How the Cloudflare API client is configured (HttpClient, API token source)
- [ ] Does this project already consume MassTransit events? Which ones?

### Discovery Task 4: Identity Service Patterns

```
Location: find the identity service (likely neavents-identity-service/)
Read: YARP routing config and auth middleware
```

Record:
- [ ] How downstream services receive tenant/user identity (headers? claims?)
- [ ] Header names (e.g., `X-User-Id`, `X-Venue-Id`, `X-User-Permissions`?)
- [ ] How a new service gets registered as a YARP route target
- [ ] Health check endpoint convention (`/healthz/live`? `/health`?)

### Discovery Task 5: Pattern Inventory

Scan any existing service (menu-service is ideal) and record:
- [ ] Feature folder structure and file naming
- [ ] `record` vs `class` usage for DTOs, commands, events
- [ ] `sealed` default? (expect yes)
- [ ] Nullable reference types enabled? (expect yes)
- [ ] Logger pattern: `ILogger<T>` with structured logging?
- [ ] Error format: RFC 7807 ProblemDetails?
- [ ] EF Core: DbContext naming, entity configuration style, migration command
- [ ] MassTransit: registration in DI, consumer registration, outbox config
- [ ] OTel: existing Activity source naming, Meter naming
- [ ] Garnet/Redis: connection pattern, key prefix conventions
- [ ] Docker Compose: how services are added, network name

**You now have all the information needed. Proceed to Phase 0.**

---

## Phase 0: Project Scaffold

### Goal
A new .NET 10 project that compiles, connects to PostgreSQL and Garnet, has a health check, and runs inside Docker Compose alongside the existing services.

### Step 0.1: Create Project

```bash
# From the repo root (adjust path based on your monorepo structure)
dotnet new webapi -n Neaslator -o src/Neaslator --no-https
```

### Step 0.2: Add NuGet Packages

Edit `src/Neaslator/Neaslator.csproj`. Add these package references:

```xml
<ItemGroup>
    <!-- EF Core + PostgreSQL -->
    <PackageReference Include="Npgsql.EntityFrameworkCore.PostgreSQL" Version="10.*" />
    <PackageReference Include="Microsoft.EntityFrameworkCore.Design" Version="10.*" />

    <!-- Garnet (via StackExchange.Redis client) -->
    <PackageReference Include="StackExchange.Redis" Version="2.*" />

    <!-- MassTransit + RabbitMQ -->
    <PackageReference Include="MassTransit.RabbitMQ" Version="8.*" />
    <PackageReference Include="MassTransit.EntityFrameworkCore" Version="8.*" />

    <!-- Resilience -->
    <PackageReference Include="Microsoft.Extensions.Http.Resilience" Version="10.*" />
    <PackageReference Include="Polly.Core" Version="8.*" />

    <!-- Hashing -->
    <PackageReference Include="System.IO.Hashing" Version="10.*" />

    <!-- OTel -->
    <PackageReference Include="OpenTelemetry.Extensions.Hosting" Version="1.*" />
    <PackageReference Include="OpenTelemetry.Instrumentation.AspNetCore" Version="1.*" />
    <PackageReference Include="OpenTelemetry.Instrumentation.Http" Version="1.*" />
    <PackageReference Include="OpenTelemetry.Exporter.OpenTelemetryProtocol" Version="1.*" />

    <!-- SignalR -->
    <PackageReference Include="Microsoft.AspNetCore.SignalR.Core" Version="10.*" />

    <!-- Validation -->
    <PackageReference Include="FluentValidation.DependencyInjectionExtensions" Version="11.*" />

    <!-- JSON -->
    <PackageReference Include="System.Text.Json" Version="10.*" />
</ItemGroup>
```

**IMPORTANT:** Check the existing services' `.csproj` files for exact version pinning strategy. If they pin to exact versions (e.g., `8.5.5`), do the same. If they use wildcard (`8.*`), match that. Also check if there is a `Directory.Packages.props` or central package management — if yes, add packages there instead.

Also add a project reference to the shared messaging contracts project:

```xml
<ItemGroup>
    <ProjectReference Include="../path/to/Neavents.Contracts/Neavents.Contracts.csproj" />
</ItemGroup>
```

Adjust the path based on Discovery Task 2.

### Step 0.3: Create Directory Structure

```
src/Neaslator/
├── Features/
│   ├── TranslateMenu/
│   ├── OnDemandTranslation/
│   ├── TranslationStatus/
│   ├── RetryFailedTranslations/
│   ├── ProviderHealth/
│   ├── TranslationMemoryStats/
│   └── QualityUpgrade/
├── Domain/
│   ├── Entities/
│   └── Enums/
├── Infrastructure/
│   ├── Normalization/
│   ├── Hashing/
│   ├── Diff/
│   ├── Cache/
│   ├── Providers/
│   └── Notifications/
├── Persistence/
│   └── Configurations/
├── Observability/
└── Program.cs
```

Create all directories. They will be populated in subsequent phases.

### Step 0.4: Domain Enums

**File: `src/Neaslator/Domain/Enums/TranslationProviderTier.cs`**

```csharp
namespace Neaslator.Domain.Enums;

public enum TranslationProviderTier : short
{
    Primary = 0,
    Secondary = 1,
    Degraded = 2
}
```

**File: `src/Neaslator/Domain/Enums/TranslationSagaState.cs`**

```csharp
namespace Neaslator.Domain.Enums;

public enum TranslationSagaState
{
    Debouncing,
    ComputingDiff,
    ResolvingCache,
    Translating,
    Completed,
    PartiallyCompleted,
    Failed,
    Superseded
}
```

**File: `src/Neaslator/Domain/Enums/TranslationUnitType.cs`**

```csharp
namespace Neaslator.Domain.Enums;

public enum TranslationUnitType
{
    SectionName,
    ItemName,
    ItemDescription
}
```

**File: `src/Neaslator/Domain/Enums/TranslationNotificationType.cs`**

```csharp
namespace Neaslator.Domain.Enums;

public enum TranslationNotificationType
{
    Started,
    Progress,
    Completed,
    PartiallyCompleted,
    Failed
}
```

### Step 0.5: Domain Entities

**File: `src/Neaslator/Domain/Entities/TranslationMemoryEntry.cs`**

```csharp
namespace Neaslator.Domain.Entities;

public sealed class TranslationMemoryEntry
{
    public long Id { get; set; }
    public long SourceHash { get; set; }
    public string NormalizedSourceText { get; set; } = default!;
    public string SourceLanguageCode { get; set; } = default!;
    public string TargetLanguageCode { get; set; } = default!;
    public string TranslatedText { get; set; } = default!;
    public TranslationProviderTier ProviderTier { get; set; }
    public string ProviderName { get; set; } = default!;
    public float ConfidenceScore { get; set; } = 1.0f;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public long HitCount { get; set; }
}
```

**File: `src/Neaslator/Domain/Entities/SupportedLanguage.cs`**

```csharp
namespace Neaslator.Domain.Entities;

public sealed class SupportedLanguage
{
    public string Code { get; set; } = default!;
    public string EnglishName { get; set; } = default!;
    public string NativeName { get; set; } = default!;
    public bool IsActive { get; set; } = true;
    public short SortOrder { get; set; }
}
```

**File: `src/Neaslator/Domain/Entities/MenuPublishSnapshot.cs`**

```csharp
namespace Neaslator.Domain.Entities;

public sealed class MenuPublishSnapshot
{
    public long Id { get; set; }
    public Guid MenuId { get; set; }     // ADAPT: change Guid to match Menu service ID type
    public Guid VenueId { get; set; }    // ADAPT: change Guid to match Venue ID type
    public string SnapshotJson { get; set; } = default!;
    public DateTimeOffset PublishedAt { get; set; }
}
```

### Step 0.6: DbContext

**File: `src/Neaslator/Persistence/NeaslatorDbContext.cs`**

```csharp
using Microsoft.EntityFrameworkCore;
using Neaslator.Domain.Entities;

namespace Neaslator.Persistence;

public sealed class NeaslatorDbContext(DbContextOptions<NeaslatorDbContext> options)
    : DbContext(options)
{
    public DbSet<TranslationMemoryEntry> TranslationMemory => Set<TranslationMemoryEntry>();
    public DbSet<SupportedLanguage> SupportedLanguages => Set<SupportedLanguage>();
    public DbSet<MenuPublishSnapshot> MenuPublishSnapshots => Set<MenuPublishSnapshot>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(NeaslatorDbContext).Assembly);
    }
}
```

**File: `src/Neaslator/Persistence/Configurations/TranslationMemoryConfiguration.cs`**

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Neaslator.Domain.Entities;

namespace Neaslator.Persistence.Configurations;

public sealed class TranslationMemoryConfiguration : IEntityTypeConfiguration<TranslationMemoryEntry>
{
    public void Configure(EntityTypeBuilder<TranslationMemoryEntry> builder)
    {
        builder.ToTable("global_translation_memory");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id)
            .UseIdentityAlwaysColumn();

        builder.Property(e => e.SourceHash).IsRequired();
        builder.Property(e => e.NormalizedSourceText).IsRequired();
        builder.Property(e => e.SourceLanguageCode).HasMaxLength(10).IsRequired();
        builder.Property(e => e.TargetLanguageCode).HasMaxLength(10).IsRequired();
        builder.Property(e => e.TranslatedText).IsRequired();
        builder.Property(e => e.ProviderTier).IsRequired();
        builder.Property(e => e.ProviderName).HasMaxLength(64).IsRequired();
        builder.Property(e => e.ConfidenceScore).HasDefaultValue(1.0f);
        builder.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
        builder.Property(e => e.UpdatedAt).HasDefaultValueSql("now()");
        builder.Property(e => e.HitCount).HasDefaultValue(0L);

        builder.HasIndex(e => new { e.SourceHash, e.SourceLanguageCode, e.TargetLanguageCode })
            .IsUnique()
            .HasDatabaseName("uq_source_target");

        builder.HasIndex(e => new { e.SourceHash, e.TargetLanguageCode })
            .HasDatabaseName("ix_gtm_lookup")
            .IncludeProperties(e => new
            {
                e.NormalizedSourceText,
                e.TranslatedText,
                e.ProviderTier,
                e.ConfidenceScore
            });

        builder.HasIndex(e => new { e.ProviderTier, e.UpdatedAt })
            .HasDatabaseName("ix_gtm_quality_upgrade")
            .HasFilter("provider_tier > 0");
    }
}
```

**File: `src/Neaslator/Persistence/Configurations/SupportedLanguageConfiguration.cs`**

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Neaslator.Domain.Entities;

namespace Neaslator.Persistence.Configurations;

public sealed class SupportedLanguageConfiguration : IEntityTypeConfiguration<SupportedLanguage>
{
    public void Configure(EntityTypeBuilder<SupportedLanguage> builder)
    {
        builder.ToTable("supported_languages");
        builder.HasKey(e => e.Code);
        builder.Property(e => e.Code).HasMaxLength(10);
        builder.Property(e => e.EnglishName).HasMaxLength(128).IsRequired();
        builder.Property(e => e.NativeName).HasMaxLength(128).IsRequired();
        builder.Property(e => e.IsActive).HasDefaultValue(true);
        builder.Property(e => e.SortOrder).HasDefaultValue((short)0);
    }
}
```

**File: `src/Neaslator/Persistence/Configurations/MenuPublishSnapshotConfiguration.cs`**

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Neaslator.Domain.Entities;

namespace Neaslator.Persistence.Configurations;

public sealed class MenuPublishSnapshotConfiguration : IEntityTypeConfiguration<MenuPublishSnapshot>
{
    public void Configure(EntityTypeBuilder<MenuPublishSnapshot> builder)
    {
        builder.ToTable("menu_publish_snapshots");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).UseIdentityAlwaysColumn();
        builder.Property(e => e.MenuId).IsRequired();
        builder.Property(e => e.VenueId).IsRequired();
        builder.Property(e => e.SnapshotJson).HasColumnType("jsonb").IsRequired();
        builder.Property(e => e.PublishedAt).HasDefaultValueSql("now()");

        builder.HasIndex(e => e.MenuId)
            .IsUnique()
            .HasDatabaseName("uq_snapshot_menu");
    }
}
```

### Step 0.7: Minimal Program.cs

```csharp
using Microsoft.EntityFrameworkCore;
using Neaslator.Persistence;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<NeaslatorDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("NeaslatorDb")));

builder.Services.AddHealthChecks()
    .AddNpgSql(builder.Configuration.GetConnectionString("NeaslatorDb")!)
    .AddRedis(builder.Configuration.GetConnectionString("Garnet")!);

WebApplication app = builder.Build();

app.MapHealthChecks("/healthz/live");
app.MapGet("/", () => Results.Ok(new { service = "neaslator", status = "running" }));

app.Run();
```

### Step 0.8: appsettings.json

```json
{
  "ConnectionStrings": {
    "NeaslatorDb": "Host=localhost;Port=5432;Database=neaslator;Username=postgres;Password=postgres",
    "Garnet": "localhost:6379"
  },
  "Neaslator": {
    "DebounceWindowSeconds": 5,
    "Providers": []
  }
}
```

ADAPT: Match the connection string format and naming from existing services.

### Step 0.9: Docker Compose

Add the Neaslator service to the existing `docker-compose.yml`. Match the pattern of existing services exactly (labels, networks, resource limits, health checks). Also ensure a Garnet service exists — if not, add one:

```yaml
  garnet:
    image: ghcr.io/microsoft/garnet:latest
    ports:
      - "6379:6379"
    networks:
      - neavents-fabric-mesh  # ADAPT: use the actual network name
    restart: always
```

ADAPT: the network name, labels, and service configuration to match Discovery Task 5.

### Step 0.10: Create Initial Migration

```bash
cd src/Neaslator
dotnet ef migrations add InitialCreate -o Persistence/Migrations
```

### Verification: Phase 0

```bash
# Build must succeed with zero warnings
dotnet build src/Neaslator

# Run the service
dotnet run --project src/Neaslator

# Health check must return 200
curl http://localhost:5000/healthz/live
# Expected: Healthy

# Root endpoint must respond
curl http://localhost:5000/
# Expected: {"service":"neaslator","status":"running"}
```

✅ Phase 0 complete when: project compiles, health check passes, connects to PostgreSQL and Garnet.

---

## Phase 1: Text Normalization + Hashing

### Goal
Zero-dependency, Span-based, allocation-minimal text processing. These are pure functions with no I/O. Unit-testable in isolation.

### Step 1.1: TextNormalizer

**File: `src/Neaslator/Infrastructure/Normalization/TextNormalizer.cs`**

```csharp
using System.Buffers;
using System.Text;

namespace Neaslator.Infrastructure.Normalization;

public static class TextNormalizer
{
    private static readonly SearchValues<char> InvisibleCharacters = SearchValues.Create(
    [
        '\u200B', '\u200C', '\u200D', '\uFEFF', '\u200E', '\u200F',
        '\u2028', '\u2029', '\u00AD', '\u034F', '\u061C', '\u2060',
        '\u2061', '\u2062', '\u2063', '\u2064', '\u206A', '\u206B',
        '\u206C', '\u206D', '\u206E', '\u206F'
    ]);

    public static string Normalize(ReadOnlySpan<char> input)
    {
        if (input.IsEmpty)
            return string.Empty;

        string nfcNormalized = new string(input).Normalize(NormalizationForm.FormC);
        ReadOnlySpan<char> source = nfcNormalized.AsSpan();

        int maxLength = source.Length;
        char[]? rentedBuffer = null;
        Span<char> buffer = maxLength <= 512
            ? stackalloc char[maxLength]
            : (rentedBuffer = ArrayPool<char>.Shared.Rent(maxLength));

        try
        {
            int written = 0;
            bool previousWasWhitespace = false;

            for (int i = 0; i < source.Length; i++)
            {
                char current = source[i];

                if (InvisibleCharacters.Contains(current))
                    continue;

                if (char.IsWhiteSpace(current))
                {
                    if (!previousWasWhitespace && written > 0)
                    {
                        buffer[written++] = ' ';
                        previousWasWhitespace = true;
                    }
                    continue;
                }

                buffer[written++] = current;
                previousWasWhitespace = false;
            }

            if (written > 0 && buffer[written - 1] == ' ')
                written--;

            return new string(buffer[..written]);
        }
        finally
        {
            if (rentedBuffer is not null)
                ArrayPool<char>.Shared.Return(rentedBuffer);
        }
    }
}
```

### Step 1.2: TranslationHasher

**File: `src/Neaslator/Infrastructure/Hashing/TranslationHasher.cs`**

```csharp
using System.Buffers;
using System.IO.Hashing;
using System.Text;

namespace Neaslator.Infrastructure.Hashing;

public static class TranslationHasher
{
    public static long ComputeHash(ReadOnlySpan<char> normalizedText)
    {
        if (normalizedText.IsEmpty)
            return 0;

        int maxByteCount = Encoding.UTF8.GetMaxByteCount(normalizedText.Length);
        byte[]? rentedBuffer = null;
        Span<byte> utf8Buffer = maxByteCount <= 1024
            ? stackalloc byte[maxByteCount]
            : (rentedBuffer = ArrayPool<byte>.Shared.Rent(maxByteCount));

        try
        {
            int bytesWritten = Encoding.UTF8.GetBytes(normalizedText, utf8Buffer);
            ulong hash = XxHash3.HashToUInt64(utf8Buffer[..bytesWritten]);
            return unchecked((long)hash);
        }
        finally
        {
            if (rentedBuffer is not null)
                ArrayPool<byte>.Shared.Return(rentedBuffer);
        }
    }
}
```

### Step 1.3: Unit Tests

**Create test project:**
```bash
dotnet new xunit -n Neaslator.Tests -o tests/Neaslator.Tests
dotnet add tests/Neaslator.Tests reference src/Neaslator
```

**File: `tests/Neaslator.Tests/Normalization/TextNormalizerTests.cs`**

```csharp
using Neaslator.Infrastructure.Normalization;

namespace Neaslator.Tests.Normalization;

public sealed class TextNormalizerTests
{
    [Fact]
    public void EmptyInput_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, TextNormalizer.Normalize("".AsSpan()));
    }

    [Fact]
    public void PlainText_ReturnsUnchanged()
    {
        Assert.Equal("Grilled Chicken", TextNormalizer.Normalize("Grilled Chicken".AsSpan()));
    }

    [Fact]
    public void MultipleSpaces_CollapsedToSingle()
    {
        Assert.Equal("Grilled Chicken", TextNormalizer.Normalize("Grilled   Chicken".AsSpan()));
    }

    [Fact]
    public void LeadingTrailingWhitespace_Trimmed()
    {
        Assert.Equal("Grilled Chicken", TextNormalizer.Normalize("  Grilled Chicken  ".AsSpan()));
    }

    [Fact]
    public void Tabs_NormalizedToSpace()
    {
        Assert.Equal("Grilled Chicken", TextNormalizer.Normalize("Grilled\tChicken".AsSpan()));
    }

    [Fact]
    public void NonBreakingSpace_NormalizedToSpace()
    {
        Assert.Equal("Grilled Chicken", TextNormalizer.Normalize("Grilled\u00A0Chicken".AsSpan()));
    }

    [Fact]
    public void ZeroWidthChars_Stripped()
    {
        Assert.Equal("Grilled Chicken", TextNormalizer.Normalize("Grilled\u200BChicken".AsSpan()));
    }

    [Fact]
    public void BOM_Stripped()
    {
        Assert.Equal("Grilled Chicken", TextNormalizer.Normalize("\uFEFFGrilled Chicken".AsSpan()));
    }

    [Fact]
    public void UnicodeCombiningAccent_NfcNormalized()
    {
        string decomposed = "caf\u0065\u0301";   // e + combining accent
        string composed = "caf\u00E9";            // single é
        Assert.Equal(TextNormalizer.Normalize(decomposed.AsSpan()),
                     TextNormalizer.Normalize(composed.AsSpan()));
    }

    [Fact]
    public void CasePreserved()
    {
        Assert.NotEqual(
            TextNormalizer.Normalize("FRENCH FRIES".AsSpan()),
            TextNormalizer.Normalize("French Fries".AsSpan()));
    }

    [Fact]
    public void DiacriticsPreserved()
    {
        Assert.NotEqual(
            TextNormalizer.Normalize("café".AsSpan()),
            TextNormalizer.Normalize("cafe".AsSpan()));
    }

    [Fact]
    public void LongText_UsesArrayPool()
    {
        string longText = new string('A', 1000) + "  " + new string('B', 1000);
        string result = TextNormalizer.Normalize(longText.AsSpan());
        Assert.Equal(new string('A', 1000) + " " + new string('B', 1000), result);
    }
}
```

**File: `tests/Neaslator.Tests/Hashing/TranslationHasherTests.cs`**

```csharp
using Neaslator.Infrastructure.Hashing;
using Neaslator.Infrastructure.Normalization;

namespace Neaslator.Tests.Hashing;

public sealed class TranslationHasherTests
{
    [Fact]
    public void EmptyInput_ReturnsZero()
    {
        Assert.Equal(0L, TranslationHasher.ComputeHash("".AsSpan()));
    }

    [Fact]
    public void SameInput_SameHash()
    {
        long hash1 = TranslationHasher.ComputeHash("Grilled Chicken".AsSpan());
        long hash2 = TranslationHasher.ComputeHash("Grilled Chicken".AsSpan());
        Assert.Equal(hash1, hash2);
    }

    [Fact]
    public void DifferentInput_DifferentHash()
    {
        long hash1 = TranslationHasher.ComputeHash("Grilled Chicken".AsSpan());
        long hash2 = TranslationHasher.ComputeHash("Fried Chicken".AsSpan());
        Assert.NotEqual(hash1, hash2);
    }

    [Fact]
    public void NormalizedEquivalents_SameHash()
    {
        string normalized1 = TextNormalizer.Normalize("Grilled  Chicken".AsSpan());
        string normalized2 = TextNormalizer.Normalize("Grilled\tChicken".AsSpan());
        Assert.Equal(
            TranslationHasher.ComputeHash(normalized1.AsSpan()),
            TranslationHasher.ComputeHash(normalized2.AsSpan()));
    }
}
```

### Verification: Phase 1

```bash
dotnet test tests/Neaslator.Tests --filter "Normalization|Hashing"
# All tests must pass
```

✅ Phase 1 complete when: all normalization and hashing tests pass.

---

## Phase 2: Cache Layer (Garnet L1 + PostgreSQL L2)

### Goal
Two-tier translation cache with collision-verified lookups, pipelined Garnet reads, batched PostgreSQL queries, and distributed thundering herd protection.

### Step 2.1: Cache Models

**File: `src/Neaslator/Infrastructure/Cache/CachedTranslation.cs`**

```csharp
using Neaslator.Domain.Enums;

namespace Neaslator.Infrastructure.Cache;

public sealed record CachedTranslation(
    string TranslatedText,
    TranslationProviderTier ProviderTier,
    float ConfidenceScore,
    string NormalizedSourceText);
```

**File: `src/Neaslator/Infrastructure/Cache/CacheLookupResult.cs`**

```csharp
namespace Neaslator.Infrastructure.Cache;

public sealed record CacheLookupResult(
    string TargetLanguageCode,
    CachedTranslation? Translation,
    CacheSource Source);

public enum CacheSource
{
    L1Garnet,
    L2PostgreSql,
    Miss
}
```

### Step 2.2: Translation Cache Service

**File: `src/Neaslator/Infrastructure/Cache/TranslationCache.cs`**

```csharp
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Neaslator.Domain.Entities;
using Neaslator.Persistence;
using StackExchange.Redis;

namespace Neaslator.Infrastructure.Cache;

public sealed class TranslationCache(
    IConnectionMultiplexer garnet,
    NeaslatorDbContext dbContext)
{
    private static string CacheKey(long sourceHash, string targetLang) =>
        $"neaslator:t:{sourceHash}:{targetLang}";

    public async Task<IReadOnlyList<CacheLookupResult>> LookupAsync(
        long sourceHash,
        string normalizedSourceText,
        string sourceLanguageCode,
        IReadOnlyList<string> targetLanguageCodes,
        CancellationToken cancellationToken)
    {
        List<CacheLookupResult> results = new(targetLanguageCodes.Count);
        List<string> l1Misses = [];

        IDatabase db = garnet.GetDatabase();

        RedisKey[] keys = new RedisKey[targetLanguageCodes.Count];
        for (int i = 0; i < targetLanguageCodes.Count; i++)
            keys[i] = CacheKey(sourceHash, targetLanguageCodes[i]);

        RedisValue[] values = await db.StringGetAsync(keys);

        for (int i = 0; i < targetLanguageCodes.Count; i++)
        {
            if (values[i].HasValue)
            {
                CachedTranslation? cached = JsonSerializer.Deserialize<CachedTranslation>(values[i]!);
                if (cached is not null &&
                    cached.NormalizedSourceText.Equals(normalizedSourceText, StringComparison.Ordinal))
                {
                    results.Add(new CacheLookupResult(targetLanguageCodes[i], cached, CacheSource.L1Garnet));
                    continue;
                }
            }
            l1Misses.Add(targetLanguageCodes[i]);
        }

        if (l1Misses.Count == 0)
            return results;

        List<TranslationMemoryEntry> l2Hits = await dbContext.TranslationMemory
            .Where(e => e.SourceHash == sourceHash
                     && e.SourceLanguageCode == sourceLanguageCode
                     && l1Misses.Contains(e.TargetLanguageCode))
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        List<KeyValuePair<RedisKey, RedisValue>> backfillBatch = [];

        foreach (TranslationMemoryEntry entry in l2Hits)
        {
            if (!entry.NormalizedSourceText.Equals(normalizedSourceText, StringComparison.Ordinal))
                continue;

            CachedTranslation cached = new(
                entry.TranslatedText,
                entry.ProviderTier,
                entry.ConfidenceScore,
                entry.NormalizedSourceText);

            results.Add(new CacheLookupResult(entry.TargetLanguageCode, cached, CacheSource.L2PostgreSql));
            l1Misses.Remove(entry.TargetLanguageCode);

            backfillBatch.Add(new(
                CacheKey(sourceHash, entry.TargetLanguageCode),
                JsonSerializer.Serialize(cached)));
        }

        if (backfillBatch.Count > 0)
            await db.StringSetAsync([.. backfillBatch]);

        foreach (string missLang in l1Misses)
            results.Add(new CacheLookupResult(missLang, null, CacheSource.Miss));

        return results;
    }

    public async Task StoreAsync(
        long sourceHash,
        string normalizedSourceText,
        string sourceLanguageCode,
        string targetLanguageCode,
        string translatedText,
        TranslationProviderTier providerTier,
        string providerName,
        float confidenceScore,
        CancellationToken cancellationToken)
    {
        TranslationMemoryEntry entry = new()
        {
            SourceHash = sourceHash,
            NormalizedSourceText = normalizedSourceText,
            SourceLanguageCode = sourceLanguageCode,
            TargetLanguageCode = targetLanguageCode,
            TranslatedText = translatedText,
            ProviderTier = providerTier,
            ProviderName = providerName,
            ConfidenceScore = confidenceScore,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        dbContext.TranslationMemory.Add(entry);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            dbContext.Entry(entry).State = EntityState.Detached;

            await dbContext.TranslationMemory
                .Where(e => e.SourceHash == sourceHash
                         && e.SourceLanguageCode == sourceLanguageCode
                         && e.TargetLanguageCode == targetLanguageCode)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(e => e.TranslatedText, translatedText)
                    .SetProperty(e => e.ProviderTier, providerTier)
                    .SetProperty(e => e.ProviderName, providerName)
                    .SetProperty(e => e.ConfidenceScore, confidenceScore)
                    .SetProperty(e => e.UpdatedAt, DateTimeOffset.UtcNow),
                    cancellationToken);
        }

        CachedTranslation cached = new(translatedText, providerTier, confidenceScore, normalizedSourceText);
        IDatabase db = garnet.GetDatabase();
        await db.StringSetAsync(
            CacheKey(sourceHash, targetLanguageCode),
            JsonSerializer.Serialize(cached));
    }

    public async Task InvalidateAsync(long sourceHash, string targetLanguageCode)
    {
        IDatabase db = garnet.GetDatabase();
        await db.KeyDeleteAsync(CacheKey(sourceHash, targetLanguageCode));
    }
}
```

### Step 2.3: Distributed Translation Lock

**File: `src/Neaslator/Infrastructure/Cache/DistributedTranslationLock.cs`**

```csharp
using System.Diagnostics;
using StackExchange.Redis;

namespace Neaslator.Infrastructure.Cache;

public sealed class DistributedTranslationLock(IConnectionMultiplexer garnet)
{
    private static readonly TimeSpan LockTtl = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

    private const string ReleaseLuaScript = """
        if redis.call('get', KEYS[1]) == ARGV[1] then
            return redis.call('del', KEYS[1])
        else
            return 0
        end
        """;

    public async Task<LockResult> TryAcquireAsync(
        long sourceHash,
        string targetLanguageCode,
        CancellationToken cancellationToken)
    {
        IDatabase db = garnet.GetDatabase();
        string lockKey = $"neaslator:lock:{sourceHash}:{targetLanguageCode}";
        string lockValue = Guid.NewGuid().ToString("N");

        bool acquired = await db.StringSetAsync(lockKey, lockValue, LockTtl, When.NotExists);

        if (acquired)
            return LockResult.Acquired(lockKey, lockValue);

        string cacheKey = $"neaslator:t:{sourceHash}:{targetLanguageCode}";
        Stopwatch stopwatch = Stopwatch.StartNew();

        while (stopwatch.Elapsed < WaitTimeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(PollInterval, cancellationToken);

            RedisValue cached = await db.StringGetAsync(cacheKey);
            if (cached.HasValue)
                return LockResult.ResolvedByPeer(cached!);
        }

        await db.StringSetAsync(lockKey, lockValue, LockTtl, When.Always);
        return LockResult.ForcedAcquisition(lockKey, lockValue);
    }

    public async Task ReleaseAsync(string lockKey, string lockValue)
    {
        IDatabase db = garnet.GetDatabase();
        await db.ScriptEvaluateAsync(
            ReleaseLuaScript,
            [new RedisKey(lockKey)],
            [new RedisValue(lockValue)]);
    }
}

public sealed record LockResult
{
    public required LockOutcome Outcome { get; init; }
    public string? LockKey { get; init; }
    public string? LockValue { get; init; }
    public string? CachedValue { get; init; }

    public static LockResult Acquired(string lockKey, string lockValue) =>
        new() { Outcome = LockOutcome.Acquired, LockKey = lockKey, LockValue = lockValue };

    public static LockResult ResolvedByPeer(string cachedValue) =>
        new() { Outcome = LockOutcome.ResolvedByPeer, CachedValue = cachedValue };

    public static LockResult ForcedAcquisition(string lockKey, string lockValue) =>
        new() { Outcome = LockOutcome.ForcedAcquisition, LockKey = lockKey, LockValue = lockValue };
}

public enum LockOutcome
{
    Acquired,
    ResolvedByPeer,
    ForcedAcquisition
}
```

### Step 2.4: Register in DI

Add to `Program.cs`:

```csharp
builder.Services.AddSingleton<IConnectionMultiplexer>(
    ConnectionMultiplexer.Connect(builder.Configuration.GetConnectionString("Garnet")!));

builder.Services.AddScoped<TranslationCache>();
builder.Services.AddSingleton<DistributedTranslationLock>();
```

### Verification: Phase 2

Write integration tests using TestContainers for PostgreSQL and Garnet. Test:
- Cache miss returns `CacheSource.Miss` for all languages
- Store then lookup returns `CacheSource.L1Garnet`
- L1 invalidation then lookup returns `CacheSource.L2PostgreSql` (and backfills L1)
- Hash collision detection (store with one source text, lookup with different text same hash → miss)
- Distributed lock: two concurrent acquires → one acquires, other waits and gets resolved

✅ Phase 2 complete when: all cache integration tests pass.

---

## Phase 3: LLM Provider Abstraction

### Goal
Pluggable provider interface, request/response contracts, one concrete provider (DeepSeek), and the Translation Router with Polly circuit breakers.

### Step 3.1: Provider Interface and Contracts

**File: `src/Neaslator/Infrastructure/Providers/ITranslationProvider.cs`**

```csharp
namespace Neaslator.Infrastructure.Providers;

/// <summary>
/// Implement this interface to add a new LLM translation provider.
/// Providers are called by TranslationRouter in priority order with circuit breaker protection.
/// </summary>
public interface ITranslationProvider
{
    string ProviderName { get; }
    TranslationProviderTier Tier { get; }
    bool SupportsPrefixCaching { get; }
    int MaxBatchSize { get; }
    int MaxConcurrentRequests { get; }
    Task<TranslationBatchResult> TranslateBatchAsync(TranslationBatchRequest request, CancellationToken cancellationToken);
    Task<bool> IsHealthyAsync(CancellationToken cancellationToken);
}
```

**File: `src/Neaslator/Infrastructure/Providers/TranslationBatchRequest.cs`**

```csharp
namespace Neaslator.Infrastructure.Providers;

public sealed record TranslationBatchRequest
{
    public required string SourceLanguageCode { get; init; }
    public required string TargetLanguageCode { get; init; }
    public required string VenueType { get; init; }
    public required string CuisineType { get; init; }
    public required string SectionName { get; init; }
    public required IReadOnlyList<TranslationBatchItem> Items { get; init; }
    public bool IsVanguardRequest { get; init; }
}

public sealed record TranslationBatchItem
{
    public required long SourceHash { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
}
```

**File: `src/Neaslator/Infrastructure/Providers/TranslationBatchResult.cs`**

```csharp
namespace Neaslator.Infrastructure.Providers;

public sealed record TranslationBatchResult
{
    public required bool IsSuccess { get; init; }
    public required IReadOnlyList<TranslatedUnit> Translations { get; init; }
    public required TokenUsage TokenUsage { get; init; }
    public string? ErrorMessage { get; init; }
    public TimeSpan Latency { get; init; }
}

public sealed record TranslatedUnit
{
    public required long SourceHash { get; init; }
    public required string TranslatedName { get; init; }
    public string? TranslatedDescription { get; init; }
    public float ConfidenceScore { get; init; } = 1.0f;
}

public sealed record TokenUsage(int InputTokens, int OutputTokens, int CachedTokens);
```

### Step 3.2: System Prompt Builder

**File: `src/Neaslator/Infrastructure/Providers/TranslationPromptBuilder.cs`**

```csharp
using System.Text.Json;

namespace Neaslator.Infrastructure.Providers;

public static class TranslationPromptBuilder
{
    public static string BuildSystemPrompt(
        string venueType,
        string cuisineType,
        string sourceLanguageName,
        string targetLanguageName)
    {
        return $"""
            You are a professional translator specializing in restaurant and hospitality menus.

            Context:
            - Venue type: {venueType}
            - Cuisine: {cuisineType}
            - Source language: {sourceLanguageName}
            - Target language: {targetLanguageName}

            Rules:
            1. Translate menu item names and descriptions naturally for the target locale.
            2. Preserve brand names, proper nouns, and culturally specific terms.
            3. For food terms with multiple meanings, use the culinary interpretation.
            4. Respond ONLY with the JSON array below. No preamble, no markdown fences.
            5. Echo each item's "hash" field exactly as provided.

            [
              {{
                "hash": <Int64>,
                "translated_name": "<string>",
                "translated_description": "<string or null>"
              }}
            ]
            """;
    }

    public static string BuildUserPayload(string sectionName, IReadOnlyList<TranslationBatchItem> items)
    {
        var payload = new
        {
            section_name = sectionName,
            items = items.Select(i => new
            {
                hash = i.SourceHash,
                name = i.Name,
                description = i.Description
            })
        };
        return JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = false });
    }
}
```

### Step 3.3: DeepSeek Provider (Reference Implementation)

**File: `src/Neaslator/Infrastructure/Providers/DeepSeekProvider.cs`**

```csharp
using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Neaslator.Infrastructure.Providers;

public sealed class DeepSeekProvider(HttpClient httpClient, DeepSeekOptions options) : ITranslationProvider
{
    public string ProviderName => "deepseek";
    public TranslationProviderTier Tier => options.Tier;
    public bool SupportsPrefixCaching => true;
    public int MaxBatchSize => options.MaxBatchSize;
    public int MaxConcurrentRequests => options.MaxConcurrentRequests;

    public async Task<TranslationBatchResult> TranslateBatchAsync(
        TranslationBatchRequest request,
        CancellationToken cancellationToken)
    {
        Stopwatch sw = Stopwatch.StartNew();

        string systemPrompt = TranslationPromptBuilder.BuildSystemPrompt(
            request.VenueType,
            request.CuisineType,
            request.SourceLanguageCode,
            request.TargetLanguageCode);

        string userPayload = TranslationPromptBuilder.BuildUserPayload(
            request.SectionName,
            request.Items);

        DeepSeekChatRequest chatRequest = new()
        {
            Model = options.Model,
            Messages =
            [
                new() { Role = "system", Content = systemPrompt },
                new() { Role = "user", Content = userPayload }
            ],
            Temperature = 0.1,
            MaxTokens = 4096,
            ResponseFormat = new() { Type = "json_object" }
        };

        HttpResponseMessage response = await httpClient.PostAsJsonAsync(
            "/chat/completions", chatRequest, cancellationToken);

        response.EnsureSuccessStatusCode();

        DeepSeekChatResponse? chatResponse = await response.Content
            .ReadFromJsonAsync<DeepSeekChatResponse>(cancellationToken);

        sw.Stop();

        if (chatResponse?.Choices is not [{ Message.Content: string rawJson }, ..])
        {
            return new TranslationBatchResult
            {
                IsSuccess = false,
                Translations = [],
                TokenUsage = new(0, 0, 0),
                ErrorMessage = "Empty response from provider",
                Latency = sw.Elapsed
            };
        }

        string cleaned = rawJson.Trim();
        if (cleaned.StartsWith("```"))
        {
            int firstNewline = cleaned.IndexOf('\n');
            int lastFence = cleaned.LastIndexOf("```");
            if (firstNewline > 0 && lastFence > firstNewline)
                cleaned = cleaned[(firstNewline + 1)..lastFence].Trim();
        }

        List<LlmTranslatedItem>? items;
        try
        {
            if (cleaned.StartsWith('{'))
            {
                JsonDocument doc = JsonDocument.Parse(cleaned);
                if (doc.RootElement.TryGetProperty("translations", out JsonElement arr))
                    items = JsonSerializer.Deserialize<List<LlmTranslatedItem>>(arr.GetRawText());
                else
                    items = JsonSerializer.Deserialize<List<LlmTranslatedItem>>("[" + cleaned + "]");
            }
            else
            {
                items = JsonSerializer.Deserialize<List<LlmTranslatedItem>>(cleaned);
            }
        }
        catch (JsonException ex)
        {
            return new TranslationBatchResult
            {
                IsSuccess = false,
                Translations = [],
                TokenUsage = ExtractTokenUsage(chatResponse),
                ErrorMessage = $"JSON parse failed: {ex.Message}",
                Latency = sw.Elapsed
            };
        }

        if (items is null || items.Count != request.Items.Count)
        {
            return new TranslationBatchResult
            {
                IsSuccess = false,
                Translations = [],
                TokenUsage = ExtractTokenUsage(chatResponse),
                ErrorMessage = $"Expected {request.Items.Count} items, got {items?.Count ?? 0}",
                Latency = sw.Elapsed
            };
        }

        HashSet<long> expectedHashes = new(request.Items.Select(i => i.SourceHash));
        List<TranslatedUnit> translations = new(items.Count);

        foreach (LlmTranslatedItem item in items)
        {
            if (!expectedHashes.Contains(item.Hash))
            {
                return new TranslationBatchResult
                {
                    IsSuccess = false,
                    Translations = [],
                    TokenUsage = ExtractTokenUsage(chatResponse),
                    ErrorMessage = $"Unexpected hash {item.Hash} in response",
                    Latency = sw.Elapsed
                };
            }

            if (string.IsNullOrWhiteSpace(item.TranslatedName))
            {
                return new TranslationBatchResult
                {
                    IsSuccess = false,
                    Translations = [],
                    TokenUsage = ExtractTokenUsage(chatResponse),
                    ErrorMessage = $"Empty translated_name for hash {item.Hash}",
                    Latency = sw.Elapsed
                };
            }

            translations.Add(new TranslatedUnit
            {
                SourceHash = item.Hash,
                TranslatedName = item.TranslatedName,
                TranslatedDescription = item.TranslatedDescription
            });
        }

        return new TranslationBatchResult
        {
            IsSuccess = true,
            Translations = translations,
            TokenUsage = ExtractTokenUsage(chatResponse),
            Latency = sw.Elapsed
        };
    }

    public async Task<bool> IsHealthyAsync(CancellationToken cancellationToken)
    {
        try
        {
            HttpResponseMessage response = await httpClient.GetAsync("/models", cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private static TokenUsage ExtractTokenUsage(DeepSeekChatResponse? response)
    {
        if (response?.Usage is null)
            return new(0, 0, 0);
        return new(
            response.Usage.PromptTokens,
            response.Usage.CompletionTokens,
            response.Usage.PromptCacheHitTokens);
    }
}

public sealed class DeepSeekOptions
{
    public string Model { get; set; } = "deepseek-chat";
    public TranslationProviderTier Tier { get; set; } = TranslationProviderTier.Primary;
    public int MaxBatchSize { get; set; } = 20;
    public int MaxConcurrentRequests { get; set; } = 50;
}

internal sealed class DeepSeekChatRequest
{
    [JsonPropertyName("model")] public string Model { get; set; } = default!;
    [JsonPropertyName("messages")] public List<DeepSeekMessage> Messages { get; set; } = [];
    [JsonPropertyName("temperature")] public double Temperature { get; set; }
    [JsonPropertyName("max_tokens")] public int MaxTokens { get; set; }
    [JsonPropertyName("response_format")] public DeepSeekResponseFormat? ResponseFormat { get; set; }
}

internal sealed class DeepSeekMessage
{
    [JsonPropertyName("role")] public string Role { get; set; } = default!;
    [JsonPropertyName("content")] public string Content { get; set; } = default!;
}

internal sealed class DeepSeekResponseFormat
{
    [JsonPropertyName("type")] public string Type { get; set; } = default!;
}

internal sealed class DeepSeekChatResponse
{
    [JsonPropertyName("choices")] public List<DeepSeekChoice>? Choices { get; set; }
    [JsonPropertyName("usage")] public DeepSeekUsage? Usage { get; set; }
}

internal sealed class DeepSeekChoice
{
    [JsonPropertyName("message")] public DeepSeekMessage Message { get; set; } = default!;
}

internal sealed class DeepSeekUsage
{
    [JsonPropertyName("prompt_tokens")] public int PromptTokens { get; set; }
    [JsonPropertyName("completion_tokens")] public int CompletionTokens { get; set; }
    [JsonPropertyName("prompt_cache_hit_tokens")] public int PromptCacheHitTokens { get; set; }
}

internal sealed class LlmTranslatedItem
{
    [JsonPropertyName("hash")] public long Hash { get; set; }
    [JsonPropertyName("translated_name")] public string TranslatedName { get; set; } = default!;
    [JsonPropertyName("translated_description")] public string? TranslatedDescription { get; set; }
}
```

### Step 3.4: Translation Router

**File: `src/Neaslator/Infrastructure/Providers/TranslationRouter.cs`**

```csharp
using System.Diagnostics;
using Neaslator.Observability;
using Polly;
using Polly.CircuitBreaker;
using Polly.RateLimiting;

namespace Neaslator.Infrastructure.Providers;

public sealed class TranslationRouter(
    IReadOnlyList<ProviderRegistration> providerChain,
    ILogger<TranslationRouter> logger)
{
    public async Task<TranslationBatchResult> TranslateAsync(
        TranslationBatchRequest request,
        CancellationToken cancellationToken)
    {
        using Activity? activity = NeaslatorActivitySources.Provider.StartActivity("TranslationRouter.Translate");
        activity?.SetTag("neaslator.source_language", request.SourceLanguageCode);
        activity?.SetTag("neaslator.target_language", request.TargetLanguageCode);
        activity?.SetTag("neaslator.batch_size", request.Items.Count);

        List<string> attemptedProviders = [];

        foreach (ProviderRegistration registration in providerChain)
        {
            if (!registration.IsAvailable)
            {
                activity?.AddEvent(new ActivityEvent("provider_skipped",
                    tags: new([ new("provider", registration.Provider.ProviderName),
                                new("reason", "circuit_open_or_rate_limited") ])));
                continue;
            }

            attemptedProviders.Add(registration.Provider.ProviderName);

            try
            {
                Stopwatch sw = Stopwatch.StartNew();

                TranslationBatchResult result = await registration.Pipeline
                    .ExecuteAsync(async token =>
                        await registration.Provider.TranslateBatchAsync(request, token),
                        cancellationToken);

                sw.Stop();

                NeaslatorMetrics.ProviderRequests.Add(1,
                    new("provider", registration.Provider.ProviderName),
                    new("status", result.IsSuccess ? "success" : "failure"));
                NeaslatorMetrics.ProviderLatencySeconds.Record(sw.Elapsed.TotalSeconds,
                    new("provider", registration.Provider.ProviderName));

                if (result.IsSuccess)
                {
                    NeaslatorMetrics.ProviderTokensUsed.Add(result.TokenUsage.InputTokens,
                        new("provider", registration.Provider.ProviderName), new("type", "input"));
                    NeaslatorMetrics.ProviderTokensUsed.Add(result.TokenUsage.OutputTokens,
                        new("provider", registration.Provider.ProviderName), new("type", "output"));
                    NeaslatorMetrics.ProviderTokensUsed.Add(result.TokenUsage.CachedTokens,
                        new("provider", registration.Provider.ProviderName), new("type", "cached"));

                    activity?.SetTag("neaslator.provider_used", registration.Provider.ProviderName);
                    return result;
                }

                logger.LogWarning("Provider {Provider} returned failure: {Error}",
                    registration.Provider.ProviderName, result.ErrorMessage);
            }
            catch (BrokenCircuitException)
            {
                logger.LogWarning("Circuit breaker opened for {Provider}",
                    registration.Provider.ProviderName);
            }
            catch (RateLimiterRejectedException)
            {
                logger.LogWarning("Rate limiter rejected for {Provider}",
                    registration.Provider.ProviderName);
            }
        }

        activity?.SetStatus(ActivityStatusCode.Error, "All providers exhausted");
        throw new InvalidOperationException(
            $"All translation providers exhausted. Attempted: [{string.Join(", ", attemptedProviders)}]");
    }
}
```

**File: `src/Neaslator/Infrastructure/Providers/ProviderRegistration.cs`**

```csharp
using Polly;

namespace Neaslator.Infrastructure.Providers;

public sealed class ProviderRegistration
{
    public required ITranslationProvider Provider { get; init; }
    public required ResiliencePipeline Pipeline { get; init; }
    public bool IsAvailable { get; set; } = true;
}
```

### Step 3.5: Register Providers in DI

This goes in `Program.cs`. Build the resilience pipeline per provider from configuration:

```csharp
// Read provider configs from appsettings
// For each configured provider, register HttpClient + build Polly pipeline + create ProviderRegistration
// Collect into IReadOnlyList<ProviderRegistration> and register TranslationRouter

builder.Services.AddHttpClient("deepseek", client =>
{
    client.BaseAddress = new Uri("https://api.deepseek.com/v1");
    client.DefaultRequestHeaders.Add("Authorization",
        $"Bearer {Environment.GetEnvironmentVariable("NEASLATOR_DEEPSEEK_API_KEY")}");
});

// Build similar for OpenAI, Anthropic, Google Translate providers as needed
// The router accepts IReadOnlyList<ProviderRegistration> — order = priority
```

ADAPT: Match how other services register HttpClients and read API keys.

### Verification: Phase 3

- Provider interface compiles
- DeepSeekProvider compiles and handles all JSON edge cases
- TranslationRouter compiles and falls through providers correctly
- Unit test: mock provider returns success → router returns it
- Unit test: mock provider throws → router tries next provider

✅ Phase 3 complete when: router correctly chains providers with fallback.

---

## Phase 4: Observability

### Goal
Activity sources, metrics counters, and OTel registration. Wired in early so every subsequent phase emits traces and metrics from the start.

### Step 4.1: Activity Sources

**File: `src/Neaslator/Observability/NeaslatorActivitySources.cs`**

```csharp
using System.Diagnostics;

namespace Neaslator.Observability;

public static class NeaslatorActivitySources
{
    public static readonly ActivitySource Pipeline = new("Neaslator.Pipeline");
    public static readonly ActivitySource Cache = new("Neaslator.Cache");
    public static readonly ActivitySource Provider = new("Neaslator.Provider");
    public static readonly ActivitySource Saga = new("Neaslator.Saga");
}
```

### Step 4.2: Metrics

**File: `src/Neaslator/Observability/NeaslatorMetrics.cs`**

```csharp
using System.Diagnostics.Metrics;

namespace Neaslator.Observability;

public static class NeaslatorMetrics
{
    private static readonly Meter Meter = new("Neaslator");

    public static readonly Counter<long> CacheLookups =
        Meter.CreateCounter<long>("neaslator.cache.lookups");
    public static readonly Counter<long> CacheCollisions =
        Meter.CreateCounter<long>("neaslator.cache.hash_collision_total");
    public static readonly Counter<long> ProviderRequests =
        Meter.CreateCounter<long>("neaslator.provider.requests_total");
    public static readonly Counter<long> ProviderTokensUsed =
        Meter.CreateCounter<long>("neaslator.provider.tokens_used");
    public static readonly Counter<double> ProviderCostCents =
        Meter.CreateCounter<double>("neaslator.provider.cost_cents");
    public static readonly Histogram<double> ProviderLatencySeconds =
        Meter.CreateHistogram<double>("neaslator.provider.latency_seconds");
    public static readonly Counter<long> ProviderFallbacks =
        Meter.CreateCounter<long>("neaslator.provider.fallbacks_total");
    public static readonly Histogram<double> SagaDurationSeconds =
        Meter.CreateHistogram<double>("neaslator.saga.duration_seconds");
    public static readonly Counter<long> SagaSuperseded =
        Meter.CreateCounter<long>("neaslator.saga.superseded_total");
    public static readonly Counter<long> ItemsProcessed =
        Meter.CreateCounter<long>("neaslator.pipeline.items_processed");
}
```

### Step 4.3: Register OTel in Program.cs

```csharp
builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService("Neaslator"))
    .WithTracing(tracing => tracing
        .AddSource("Neaslator.Pipeline")
        .AddSource("Neaslator.Cache")
        .AddSource("Neaslator.Provider")
        .AddSource("Neaslator.Saga")
        .AddSource("MassTransit")
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddOtlpExporter())
    .WithMetrics(metrics => metrics
        .AddMeter("Neaslator")
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddOtlpExporter());
```

ADAPT: Match the OTel exporter configuration from existing services (endpoint URL, protocol, etc.).

### Verification: Phase 4

- Traces appear in SigNoz when the service starts and receives requests
- Metrics are visible in SigNoz metrics explorer

✅ Phase 4 complete when: OTel data flows to SigNoz.

---

## Phase 5: Diff Engine

### Goal
Compare current menu state against last-published snapshot. Output only the items whose translatable text has changed.

### Step 5.1: Read the Menu Service Domain (Discovery Task 1 Results)

You now need to use the entity names and property names you recorded. The code below uses placeholders — replace them with the actual names from the Menu service.

### Step 5.2: Snapshot Model

The snapshot captures the translatable text of every section and item at publish time. It is stored as JSONB.

**File: `src/Neaslator/Infrastructure/Diff/MenuSnapshot.cs`**

```csharp
namespace Neaslator.Infrastructure.Diff;

public sealed record MenuSnapshot
{
    public required IReadOnlyList<SectionSnapshot> Sections { get; init; }
}

public sealed record SectionSnapshot
{
    public required Guid Id { get; init; }       // ADAPT: match Menu service ID type
    public required string Name { get; init; }
    public required IReadOnlyList<ItemSnapshot> Items { get; init; }
}

public sealed record ItemSnapshot
{
    public required Guid Id { get; init; }       // ADAPT: match Menu service ID type
    public required string Name { get; init; }
    public string? Description { get; init; }
}
```

### Step 5.3: Translation Unit

**File: `src/Neaslator/Infrastructure/Diff/TranslationUnit.cs`**

```csharp
using Neaslator.Domain.Enums;

namespace Neaslator.Infrastructure.Diff;

public sealed record TranslationUnit
{
    public required long SourceHash { get; init; }
    public required string NormalizedSourceText { get; init; }
    public required TranslationUnitType UnitType { get; init; }
    public required Guid ParentSectionId { get; init; } // ADAPT: match Menu service ID type
    public required Guid ItemId { get; init; }          // ADAPT: match Menu service ID type
}
```

### Step 5.4: Diff Engine

**File: `src/Neaslator/Infrastructure/Diff/DiffEngine.cs`**

```csharp
using Neaslator.Domain.Enums;
using Neaslator.Infrastructure.Hashing;
using Neaslator.Infrastructure.Normalization;

namespace Neaslator.Infrastructure.Diff;

public static class DiffEngine
{
    public static IReadOnlyList<TranslationUnit> ComputeDiff(
        MenuSnapshot currentSnapshot,
        MenuSnapshot? previousSnapshot)
    {
        List<TranslationUnit> units = [];

        if (previousSnapshot is null)
        {
            foreach (SectionSnapshot section in currentSnapshot.Sections)
            {
                AddSectionUnit(units, section);
                foreach (ItemSnapshot item in section.Items)
                    AddItemUnits(units, item, section.Id);
            }
            return units;
        }

        Dictionary<Guid, SectionSnapshot> previousSections =
            previousSnapshot.Sections.ToDictionary(s => s.Id);

        foreach (SectionSnapshot currentSection in currentSnapshot.Sections)
        {
            if (!previousSections.TryGetValue(currentSection.Id, out SectionSnapshot? prevSection))
            {
                AddSectionUnit(units, currentSection);
                foreach (ItemSnapshot item in currentSection.Items)
                    AddItemUnits(units, item, currentSection.Id);
                continue;
            }

            string currentSectionNorm = TextNormalizer.Normalize(currentSection.Name);
            string prevSectionNorm = TextNormalizer.Normalize(prevSection.Name);
            if (!currentSectionNorm.Equals(prevSectionNorm, StringComparison.Ordinal))
                AddSectionUnit(units, currentSection);

            Dictionary<Guid, ItemSnapshot> previousItems =
                prevSection.Items.ToDictionary(i => i.Id);

            foreach (ItemSnapshot currentItem in currentSection.Items)
            {
                if (!previousItems.TryGetValue(currentItem.Id, out ItemSnapshot? prevItem))
                {
                    AddItemUnits(units, currentItem, currentSection.Id);
                    continue;
                }

                string curName = TextNormalizer.Normalize(currentItem.Name);
                string prevName = TextNormalizer.Normalize(prevItem.Name);
                string curDesc = TextNormalizer.Normalize((currentItem.Description ?? "").AsSpan());
                string prevDesc = TextNormalizer.Normalize((prevItem.Description ?? "").AsSpan());

                if (!curName.Equals(prevName, StringComparison.Ordinal))
                    AddNameUnit(units, currentItem, currentSection.Id);

                if (!curDesc.Equals(prevDesc, StringComparison.Ordinal) &&
                    !string.IsNullOrEmpty(currentItem.Description))
                    AddDescriptionUnit(units, currentItem, currentSection.Id);
            }
        }

        return units;
    }

    private static void AddSectionUnit(List<TranslationUnit> units, SectionSnapshot section)
    {
        string normalized = TextNormalizer.Normalize(section.Name);
        units.Add(new TranslationUnit
        {
            SourceHash = TranslationHasher.ComputeHash(normalized),
            NormalizedSourceText = normalized,
            UnitType = TranslationUnitType.SectionName,
            ParentSectionId = section.Id,
            ItemId = Guid.Empty
        });
    }

    private static void AddItemUnits(List<TranslationUnit> units, ItemSnapshot item, Guid sectionId)
    {
        AddNameUnit(units, item, sectionId);
        if (!string.IsNullOrEmpty(item.Description))
            AddDescriptionUnit(units, item, sectionId);
    }

    private static void AddNameUnit(List<TranslationUnit> units, ItemSnapshot item, Guid sectionId)
    {
        string normalized = TextNormalizer.Normalize(item.Name);
        units.Add(new TranslationUnit
        {
            SourceHash = TranslationHasher.ComputeHash(normalized),
            NormalizedSourceText = normalized,
            UnitType = TranslationUnitType.ItemName,
            ParentSectionId = sectionId,
            ItemId = item.Id
        });
    }

    private static void AddDescriptionUnit(List<TranslationUnit> units, ItemSnapshot item, Guid sectionId)
    {
        string normalized = TextNormalizer.Normalize((item.Description ?? "").AsSpan());
        units.Add(new TranslationUnit
        {
            SourceHash = TranslationHasher.ComputeHash(normalized),
            NormalizedSourceText = normalized,
            UnitType = TranslationUnitType.ItemDescription,
            ParentSectionId = sectionId,
            ItemId = item.Id
        });
    }
}
```

### Verification: Phase 5

Unit tests:
- Null previous snapshot → all items returned
- Identical snapshots → empty diff
- One item name changed → only that item's name unit returned
- New item added → that item returned
- Item deleted → nothing returned (deletion is not a translation trigger)
- Section name changed → section unit returned
- Item reordered → empty diff

✅ Phase 5 complete when: all diff engine tests pass.

---

## Phase 6: Messaging Contracts & Events

### Goal
Define the events Neaslator publishes. These go in the shared messaging contracts project so downstream consumers (edge API, webhooks, etc.) can subscribe.

### Step 6.1: Read Existing Contracts (Discovery Task 2 Results)

Open the shared contracts project. Match the namespace, naming convention, and property types.

### Step 6.2: Add Contracts

**Add to the shared contracts project (not in Neaslator):**

```csharp
// ADAPT: namespace to match existing contract conventions

public sealed record MenuTranslationCompletedEvent
{
    public required Guid MenuId { get; init; }          // ADAPT: ID type
    public required Guid VenueId { get; init; }         // ADAPT: ID type
    public required string SourceLanguageCode { get; init; }
    public required IReadOnlyList<LanguageTranslationResult> Results { get; init; }
    public required int TotalLanguages { get; init; }
    public required int CompletedLanguages { get; init; }
    public required int FailedLanguages { get; init; }
    public required DateTimeOffset CompletedAt { get; init; }
}

public sealed record LanguageTranslationResult
{
    public required string TargetLanguageCode { get; init; }
    public required bool IsSuccess { get; init; }
    public string? ErrorMessage { get; init; }
    public required IReadOnlyList<TranslatedMenuItem> Translations { get; init; }
}

public sealed record TranslatedMenuItem
{
    public required Guid ItemId { get; init; }          // ADAPT: ID type
    public required Guid SectionId { get; init; }       // ADAPT: ID type
    public required string TranslatedName { get; init; }
    public string? TranslatedDescription { get; init; }
}
```

**Also ensure `MenuPublishedEvent` exists in the contracts. If not, add it:**

```csharp
public sealed record MenuPublishedEvent
{
    public required Guid VenueId { get; init; }         // ADAPT: ID type
    public required Guid MenuId { get; init; }          // ADAPT: ID type
    public required DateTimeOffset PublishedAt { get; init; }
    public required string SourceLanguageCode { get; init; }
    public required string VenueType { get; init; }
    public required string CuisineType { get; init; }
}
```

CRITICAL: If `MenuPublishedEvent` already exists but lacks `SourceLanguageCode`, `VenueType`, or `CuisineType`, discuss with the codebase owner before modifying a shared contract. These fields may need to be added to the publisher side too.

### Verification: Phase 6

- Contracts project compiles
- Neaslator project references contracts and compiles

✅ Phase 6 complete when: shared contracts are defined and both projects compile.

---

## Phase 7: Translation Pipeline Orchestrator

### Goal
The core orchestration logic that ties together: diff → cache resolution → LLM batching → storage → event publishing. This is not the saga — this is the pure business logic the saga calls.

### Step 7.1: Pipeline Orchestrator

**File: `src/Neaslator/Features/TranslateMenu/TranslationPipeline.cs`**

This is the largest single file. It orchestrates the full translation flow for a single menu publish:

```csharp
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Neaslator.Domain.Entities;
using Neaslator.Infrastructure.Cache;
using Neaslator.Infrastructure.Diff;
using Neaslator.Infrastructure.Providers;
using Neaslator.Observability;
using Neaslator.Persistence;

namespace Neaslator.Features.TranslateMenu;

public sealed class TranslationPipeline(
    NeaslatorDbContext dbContext,
    TranslationCache cache,
    TranslationRouter router,
    ILogger<TranslationPipeline> logger)
{
    public async Task<TranslationPipelineResult> ExecuteAsync(
        MenuSnapshot currentSnapshot,
        MenuSnapshot? previousSnapshot,
        string sourceLanguageCode,
        string venueType,
        string cuisineType,
        CancellationToken cancellationToken)
    {
        using Activity? activity = NeaslatorActivitySources.Pipeline.StartActivity("TranslationPipeline.Execute");

        // 1. Compute diff
        IReadOnlyList<TranslationUnit> changedUnits;
        using (Activity? diffActivity = NeaslatorActivitySources.Pipeline.StartActivity("compute_diff"))
        {
            changedUnits = DiffEngine.ComputeDiff(currentSnapshot, previousSnapshot);
            diffActivity?.SetTag("neaslator.changed_items", changedUnits.Count);
        }

        if (changedUnits.Count == 0)
        {
            return new TranslationPipelineResult
            {
                TotalLanguages = 0,
                CompletedLanguages = 0,
                FailedLanguages = 0,
                Results = []
            };
        }

        // 2. Get active target languages
        List<SupportedLanguage> targetLanguages = await dbContext.SupportedLanguages
            .Where(l => l.IsActive && l.Code != sourceLanguageCode)
            .OrderBy(l => l.SortOrder)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        List<string> targetCodes = targetLanguages.Select(l => l.Code).ToList();

        // 3. Cache resolution for each changed unit
        Dictionary<string, List<TranslationUnit>> cacheMissesByLanguage = [];

        using (Activity? cacheActivity = NeaslatorActivitySources.Cache.StartActivity("resolve_cache"))
        {
            foreach (TranslationUnit unit in changedUnits)
            {
                IReadOnlyList<CacheLookupResult> lookupResults = await cache.LookupAsync(
                    unit.SourceHash,
                    unit.NormalizedSourceText,
                    sourceLanguageCode,
                    targetCodes,
                    cancellationToken);

                foreach (CacheLookupResult result in lookupResults)
                {
                    NeaslatorMetrics.CacheLookups.Add(1,
                        new("level", result.Source == CacheSource.L1Garnet ? "l1" : result.Source == CacheSource.L2PostgreSql ? "l2" : "miss"),
                        new("result", result.Translation is not null ? "hit" : "miss"));

                    if (result.Source == CacheSource.Miss)
                    {
                        if (!cacheMissesByLanguage.TryGetValue(result.TargetLanguageCode, out List<TranslationUnit>? misses))
                        {
                            misses = [];
                            cacheMissesByLanguage[result.TargetLanguageCode] = misses;
                        }
                        misses.Add(unit);
                    }
                    else
                    {
                        NeaslatorMetrics.ItemsProcessed.Add(1,
                            new("source", result.Source == CacheSource.L1Garnet ? "cache_l1" : "cache_l2"));
                    }
                }
            }

            cacheActivity?.SetTag("neaslator.cache_miss_languages", cacheMissesByLanguage.Count);
        }

        if (cacheMissesByLanguage.Count == 0)
        {
            return new TranslationPipelineResult
            {
                TotalLanguages = targetCodes.Count,
                CompletedLanguages = targetCodes.Count,
                FailedLanguages = 0,
                Results = targetCodes.Select(c => new LanguageResult
                {
                    TargetLanguageCode = c,
                    IsSuccess = true
                }).ToList()
            };
        }

        // 4. LLM translation for cache misses
        List<LanguageResult> results = [];
        int completedCount = targetCodes.Count - cacheMissesByLanguage.Count;
        int failedCount = 0;

        // Add already-resolved languages
        foreach (string code in targetCodes)
        {
            if (!cacheMissesByLanguage.ContainsKey(code))
                results.Add(new LanguageResult { TargetLanguageCode = code, IsSuccess = true });
        }

        // Determine if Vanguard strategy applies
        bool useVanguard = router is not null && cacheMissesByLanguage.Count > 10;
        // ADAPT: check provider.SupportsPrefixCaching and prefix token count

        using (Activity? translateActivity = NeaslatorActivitySources.Provider.StartActivity("fan_out_translations"))
        {
            translateActivity?.SetTag("neaslator.language_count", cacheMissesByLanguage.Count);

            // Fan out translations per language (parallel, bounded by provider MaxConcurrentRequests)
            SemaphoreSlim concurrencyLimiter = new(20);

            Task[] translationTasks = cacheMissesByLanguage.Select(async kvp =>
            {
                await concurrencyLimiter.WaitAsync(cancellationToken);
                try
                {
                    string targetLang = kvp.Key;
                    List<TranslationUnit> units = kvp.Value;

                    // Batch by section (max 20 items per batch)
                    // For now, send all as one batch with a generic section name
                    TranslationBatchRequest request = new()
                    {
                        SourceLanguageCode = sourceLanguageCode,
                        TargetLanguageCode = targetLang,
                        VenueType = venueType,
                        CuisineType = cuisineType,
                        SectionName = "Menu",
                        Items = units.Select(u => new TranslationBatchItem
                        {
                            SourceHash = u.SourceHash,
                            Name = u.NormalizedSourceText,
                            Description = null
                        }).ToList()
                    };

                    try
                    {
                        TranslationBatchResult batchResult = await router.TranslateAsync(request, cancellationToken);

                        if (batchResult.IsSuccess)
                        {
                            foreach (TranslatedUnit translated in batchResult.Translations)
                            {
                                TranslationUnit? matchingUnit = units.FirstOrDefault(u => u.SourceHash == translated.SourceHash);
                                if (matchingUnit is null) continue;

                                await cache.StoreAsync(
                                    translated.SourceHash,
                                    matchingUnit.NormalizedSourceText,
                                    sourceLanguageCode,
                                    targetLang,
                                    translated.TranslatedName,
                                    router is not null ? TranslationProviderTier.Primary : TranslationProviderTier.Degraded,
                                    "deepseek",
                                    translated.ConfidenceScore,
                                    cancellationToken);

                                NeaslatorMetrics.ItemsProcessed.Add(1, new("source", "provider"));
                            }

                            lock (results)
                            {
                                results.Add(new LanguageResult { TargetLanguageCode = targetLang, IsSuccess = true });
                                Interlocked.Increment(ref completedCount);
                            }
                        }
                        else
                        {
                            lock (results)
                            {
                                results.Add(new LanguageResult
                                {
                                    TargetLanguageCode = targetLang,
                                    IsSuccess = false,
                                    ErrorMessage = batchResult.ErrorMessage
                                });
                                Interlocked.Increment(ref failedCount);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Translation failed for language {Language}", targetLang);
                        lock (results)
                        {
                            results.Add(new LanguageResult
                            {
                                TargetLanguageCode = targetLang,
                                IsSuccess = false,
                                ErrorMessage = ex.Message
                            });
                            Interlocked.Increment(ref failedCount);
                        }
                    }
                }
                finally
                {
                    concurrencyLimiter.Release();
                }
            }).ToArray();

            await Task.WhenAll(translationTasks);
        }

        return new TranslationPipelineResult
        {
            TotalLanguages = targetCodes.Count,
            CompletedLanguages = completedCount,
            FailedLanguages = failedCount,
            Results = results
        };
    }
}

public sealed class TranslationPipelineResult
{
    public required int TotalLanguages { get; init; }
    public required int CompletedLanguages { get; init; }
    public required int FailedLanguages { get; init; }
    public required IReadOnlyList<LanguageResult> Results { get; init; }
}

public sealed class LanguageResult
{
    public required string TargetLanguageCode { get; init; }
    public required bool IsSuccess { get; init; }
    public string? ErrorMessage { get; init; }
}
```

### Verification: Phase 7

- Pipeline compiles
- Integration test with mocked provider: 3 items, 2 languages, 1 cached 1 not → correct fan-out
- All cache hits → no provider calls
- All misses → provider called for each language

✅ Phase 7 complete when: pipeline orchestration works end-to-end with test doubles.

---

## Phase 8: MassTransit Saga & Consumers

### Goal
Wire the pipeline into MassTransit. Consume `MenuPublishedEvent`, run the debounce + saga state machine, publish `MenuTranslationCompletedEvent` on completion.

### Step 8.1: Read Existing MassTransit Patterns (Discovery Task 5)

Before writing consumers, read how other services register MassTransit, configure the outbox, and define sagas. Match that pattern exactly.

### Step 8.2: Menu Published Consumer (Debounce Entry Point)

**File: `src/Neaslator/Features/TranslateMenu/MenuPublishedConsumer.cs`**

```csharp
using MassTransit;
using StackExchange.Redis;
// ADAPT: import the actual MenuPublishedEvent from the contracts project

namespace Neaslator.Features.TranslateMenu;

public sealed class MenuPublishedConsumer(
    IConnectionMultiplexer garnet,
    IPublishEndpoint publishEndpoint,
    ILogger<MenuPublishedConsumer> logger) : IConsumer<MenuPublishedEvent>
{
    private static readonly TimeSpan DebounceWindow = TimeSpan.FromSeconds(5);

    public async Task Consume(ConsumeContext<MenuPublishedEvent> context)
    {
        IDatabase db = garnet.GetDatabase();
        string debounceKey = $"neaslator:debounce:{context.Message.MenuId}";

        bool isFirst = await db.StringSetAsync(
            debounceKey,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            DebounceWindow,
            When.NotExists);

        if (!isFirst)
        {
            await db.KeyExpireAsync(debounceKey, DebounceWindow);
            logger.LogInformation("Debounce coalesced for menu {MenuId}", context.Message.MenuId);
            return;
        }

        await context.SchedulePublish(
            DebounceWindow,
            new StartTranslationCommand
            {
                MenuId = context.Message.MenuId,
                VenueId = context.Message.VenueId,
                SourceLanguageCode = context.Message.SourceLanguageCode,
                VenueType = context.Message.VenueType,
                CuisineType = context.Message.CuisineType,
                TriggeredAt = context.Message.PublishedAt
            });
    }
}

public sealed record StartTranslationCommand
{
    public required Guid MenuId { get; init; }      // ADAPT: ID type
    public required Guid VenueId { get; init; }     // ADAPT: ID type
    public required string SourceLanguageCode { get; init; }
    public required string VenueType { get; init; }
    public required string CuisineType { get; init; }
    public required DateTimeOffset TriggeredAt { get; init; }
}
```

### Step 8.3: Start Translation Consumer (Runs the Pipeline)

**File: `src/Neaslator/Features/TranslateMenu/StartTranslationConsumer.cs`**

```csharp
using System.Text.Json;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Neaslator.Domain.Entities;
using Neaslator.Infrastructure.Diff;
using Neaslator.Persistence;
// ADAPT: import MenuTranslationCompletedEvent, LanguageTranslationResult, TranslatedMenuItem

namespace Neaslator.Features.TranslateMenu;

public sealed class StartTranslationConsumer(
    TranslationPipeline pipeline,
    NeaslatorDbContext dbContext,
    IPublishEndpoint publishEndpoint,
    ILogger<StartTranslationConsumer> logger) : IConsumer<StartTranslationCommand>
{
    public async Task Consume(ConsumeContext<StartTranslationCommand> context)
    {
        StartTranslationCommand command = context.Message;

        logger.LogInformation("Starting translation for menu {MenuId}", command.MenuId);

        // ADAPT: This section needs to build a MenuSnapshot from the current menu state.
        // You must query the Menu service's database OR receive the menu data in the event.
        //
        // Option A: Neaslator has read access to the menu service database (shared DB or read replica)
        // Option B: The MenuPublishedEvent carries the full menu payload
        // Option C: Neaslator calls a Menu service API endpoint to fetch the menu
        //
        // DECIDE which option fits the existing architecture and implement accordingly.
        // Below is a placeholder using Option A (direct DB read).

        MenuSnapshot currentSnapshot = await BuildCurrentSnapshotAsync(command.MenuId, context.CancellationToken);

        MenuPublishSnapshot? previousSnapshotEntity = await dbContext.MenuPublishSnapshots
            .FirstOrDefaultAsync(s => s.MenuId == command.MenuId, context.CancellationToken);

        MenuSnapshot? previousSnapshot = previousSnapshotEntity is not null
            ? JsonSerializer.Deserialize<MenuSnapshot>(previousSnapshotEntity.SnapshotJson)
            : null;

        TranslationPipelineResult result = await pipeline.ExecuteAsync(
            currentSnapshot,
            previousSnapshot,
            command.SourceLanguageCode,
            command.VenueType,
            command.CuisineType,
            context.CancellationToken);

        // Save new snapshot
        string snapshotJson = JsonSerializer.Serialize(currentSnapshot);
        if (previousSnapshotEntity is not null)
        {
            previousSnapshotEntity.SnapshotJson = snapshotJson;
            previousSnapshotEntity.PublishedAt = DateTimeOffset.UtcNow;
        }
        else
        {
            dbContext.MenuPublishSnapshots.Add(new MenuPublishSnapshot
            {
                MenuId = command.MenuId,
                VenueId = command.VenueId,
                SnapshotJson = snapshotJson,
                PublishedAt = DateTimeOffset.UtcNow
            });
        }
        await dbContext.SaveChangesAsync(context.CancellationToken);

        // Publish completion event for downstream consumers (edge API, webhooks, etc.)
        await publishEndpoint.Publish(new MenuTranslationCompletedEvent
        {
            MenuId = command.MenuId,
            VenueId = command.VenueId,
            SourceLanguageCode = command.SourceLanguageCode,
            TotalLanguages = result.TotalLanguages,
            CompletedLanguages = result.CompletedLanguages,
            FailedLanguages = result.FailedLanguages,
            CompletedAt = DateTimeOffset.UtcNow,
            Results = result.Results.Select(r => new LanguageTranslationResult
            {
                TargetLanguageCode = r.TargetLanguageCode,
                IsSuccess = r.IsSuccess,
                ErrorMessage = r.ErrorMessage,
                Translations = []  // ADAPT: populate with actual translated items for downstream use
            }).ToList()
        }, context.CancellationToken);

        logger.LogInformation(
            "Translation completed for menu {MenuId}: {Completed}/{Total} languages, {Failed} failed",
            command.MenuId, result.CompletedLanguages, result.TotalLanguages, result.FailedLanguages);
    }

    private async Task<MenuSnapshot> BuildCurrentSnapshotAsync(Guid menuId, CancellationToken ct)
    {
        // ADAPT: Replace this with actual menu data retrieval.
        // Read from the menu service's database, API, or event payload.
        // Build a MenuSnapshot from the real Menu → Section → Item hierarchy.
        throw new NotImplementedException(
            "IMPLEMENT: Build MenuSnapshot from actual menu data source. " +
            "See Discovery Task 1 for the menu entity structure.");
    }
}
```

### Step 8.4: Register MassTransit in Program.cs

```csharp
builder.Services.AddMassTransit(cfg =>
{
    cfg.AddConsumer<MenuPublishedConsumer>();
    cfg.AddConsumer<StartTranslationConsumer>();

    cfg.UsingRabbitMq((context, rabbit) =>
    {
        rabbit.Host(builder.Configuration.GetConnectionString("RabbitMq"));

        // ADAPT: Match the existing MassTransit configuration pattern
        // (message scheduler, outbox, endpoint naming, etc.)

        rabbit.ConfigureEndpoints(context);
    });

    // ADAPT: Add EF Core outbox if existing services use it
    // cfg.AddEntityFrameworkOutbox<NeaslatorDbContext>(o => { ... });
});
```

ADAPT: Match the exact MassTransit registration pattern from existing services. Check for message scheduler (Quartz, Hangfire, or delayed exchange), outbox configuration, and endpoint naming conventions.

### Verification: Phase 8

- Service starts and connects to RabbitMQ
- Publish a test `MenuPublishedEvent` manually → debounce timer fires → `StartTranslationConsumer` receives it
- The `BuildCurrentSnapshotAsync` will throw `NotImplementedException` — that is expected at this phase. The wiring is what we verify.

✅ Phase 8 complete when: MassTransit consumers are registered and the message flow works end-to-end (minus the actual menu data retrieval).

---

## Phase 9: SignalR Notifications

**File: `src/Neaslator/Infrastructure/Notifications/TranslationHub.cs`**

```csharp
using Microsoft.AspNetCore.SignalR;

namespace Neaslator.Infrastructure.Notifications;

public sealed class TranslationHub : Hub
{
    public async Task JoinVenueGroup(Guid venueId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, $"venue:{venueId}");
    }
}
```

**File: `src/Neaslator/Infrastructure/Notifications/TranslationNotifier.cs`**

```csharp
using Microsoft.AspNetCore.SignalR;
using Neaslator.Domain.Enums;

namespace Neaslator.Infrastructure.Notifications;

public sealed class TranslationNotifier(IHubContext<TranslationHub> hubContext)
{
    public async Task NotifyAsync(Guid venueId, TranslationStatusNotification notification)
    {
        await hubContext.Clients
            .Group($"venue:{venueId}")
            .SendAsync("TranslationStatus", notification);
    }
}

public sealed record TranslationStatusNotification
{
    public required Guid MenuId { get; init; }
    public required TranslationNotificationType Type { get; init; }
    public required int TotalLanguages { get; init; }
    public required int CompletedLanguages { get; init; }
    public required int FailedLanguages { get; init; }
    public string? ErrorSummary { get; init; }
}
```

Register in `Program.cs`:
```csharp
builder.Services.AddSignalR();
builder.Services.AddScoped<TranslationNotifier>();

// In app configuration:
app.MapHub<TranslationHub>("/hubs/translation");
```

Then inject `TranslationNotifier` into `StartTranslationConsumer` and call it at key points (started, progress, completed).

### Verification: Phase 9

- SignalR hub endpoint responds at `/hubs/translation`
- Test client can connect and join a venue group

✅ Phase 9 complete when: SignalR hub is reachable and group membership works.

---

## Phase 10: REST Endpoints

Create these as minimal API endpoints or controllers (match existing service pattern):

| Method | Path | Feature Folder |
|--------|------|----------------|
| `POST` | `/api/v1/translate/on-demand` | `OnDemandTranslation/` |
| `GET` | `/api/v1/translate/menu/{menuId}/status` | `TranslationStatus/` |
| `POST` | `/api/v1/translate/menu/{menuId}/retry` | `RetryFailedTranslations/` |
| `GET` | `/api/v1/providers/health` | `ProviderHealth/` |
| `GET` | `/api/v1/translate/memory/stats` | `TranslationMemoryStats/` |
| `GET` | `/api/v1/languages` | Inline in `Program.cs` or small feature |

Each endpoint follows the vertical slice pattern: Command/Query → Handler → Validator → Endpoint → Response, all in one feature folder. Handlers call `NeaslatorDbContext` directly.

Auth on all endpoints: use the same auth middleware/headers discovered in Discovery Task 4.

The on-demand endpoint reuses `TranslationCache` and `TranslationRouter` — a single-item synchronous translation that also populates the cache for future saga runs.

### Verification: Phase 10

- All endpoints respond with correct status codes
- On-demand endpoint performs a real translation (cache miss → LLM → cache store → response)

✅ Phase 10 complete when: all REST endpoints work and return correct responses.

---

## Phase 11: Quality Upgrade Job

**File: `src/Neaslator/Features/QualityUpgrade/QualityUpgradeJob.cs`**

A scheduled background job that finds translations produced by degraded providers and re-translates them using the primary provider. Run during off-peak hours.

Implementation: find entries where `provider_tier > 0`, batch them by target language, send through `TranslationRouter`, update the entries, invalidate L1 cache.

Register as a MassTransit recurring job, Hangfire job, or `IHostedService` with a timer — match whatever scheduling pattern exists in the codebase.

### Verification: Phase 11

- Job runs and identifies degraded entries
- Entries are upgraded and cache is invalidated

✅ Phase 11 complete when: degraded translations get automatically upgraded.

---

## Phase 12: Benchmarks

**Create project:**
```bash
dotnet new console -n Neaslator.Benchmarks -o tests/Neaslator.Benchmarks
dotnet add tests/Neaslator.Benchmarks package BenchmarkDotNet
dotnet add tests/Neaslator.Benchmarks reference src/Neaslator
```

Benchmark `TextNormalizer.Normalize` and `TranslationHasher.ComputeHash` with various input sizes.

**Performance targets:**
- Normalization: < 1μs per item for typical menu text (< 200 chars)
- Hashing: < 500ns per item
- Full cache hit path (Garnet L1): < 1ms per item batch

---

## Phase 13: Edge API Integration (DOWNSTREAM — NOT IN NEASLATOR)

This phase is implemented in the **existing edge API project**, not in Neaslator.

### Goal

A MassTransit consumer in the edge API that subscribes to `MenuTranslationCompletedEvent` and writes translated menu documents to Cloudflare KV.

### Step 13.1: Read Existing Edge API (Discovery Task 3)

Open the edge API project. Understand:
- The current KV key format
- The current menu JSON structure
- The Cloudflare API client implementation

### Step 13.2: Add Consumer

In the edge API project, add a consumer for `MenuTranslationCompletedEvent`:

```csharp
// In the edge API project, NOT in Neaslator

public sealed class MenuTranslationCompletedConsumer(
    /* inject: Cloudflare KV client, menu data source, translation memory query */
    ) : IConsumer<MenuTranslationCompletedEvent>
{
    public async Task Consume(ConsumeContext<MenuTranslationCompletedEvent> context)
    {
        MenuTranslationCompletedEvent message = context.Message;

        // For each completed language:
        // 1. Build the per-language menu JSON document (same shape the Worker reads)
        // 2. Key format: {existingPrefix}:{languageCode}
        // 3. Add to bulk write batch

        // Execute Cloudflare KV bulk write (same pattern already used in this project)

        // Log completion
    }
}
```

The consumer queries the translation memory (or uses data from the event) to build the translated JSON documents, then writes to KV using the existing Cloudflare client in the edge API project.

**This consumer is the integration point between Neaslator (generic translation engine) and your specific use case (Cloudflare KV edge delivery).** Another team could write a different consumer that pushes to a database, sends webhooks, or updates a CDN — Neaslator doesn't know or care.

### Verification: Phase 13

- Publish a `MenuTranslationCompletedEvent` manually
- Consumer picks it up and writes to Cloudflare KV
- Cloudflare Worker serves the translated menu

✅ Phase 13 complete when: translated menus are served from the edge in multiple languages.

---

## Final Checklist

After all phases pass:

- [ ] All unit tests pass (`dotnet test`)
- [ ] All integration tests pass (TestContainers)
- [ ] Benchmarks meet performance targets
- [ ] Service starts in Docker Compose alongside existing services
- [ ] Health check passes at `/healthz/live`
- [ ] OTel traces flow to SigNoz with full span hierarchy
- [ ] Metrics visible in SigNoz (cache hit ratio, provider latency, cost tracking)
- [ ] Publish a real menu → debounce → diff → cache → LLM → store → event → KV write → SignalR
- [ ] On-demand REST endpoint translates a single item
- [ ] Provider fallback works (disable primary → traffic routes to secondary)
- [ ] Concurrent publishes coalesce during debounce window
- [ ] Quality upgrade job runs and upgrades degraded translations
- [ ] No `var` keyword abuse, no unnecessary comments, `sealed` on everything, `record` for DTOs/events
