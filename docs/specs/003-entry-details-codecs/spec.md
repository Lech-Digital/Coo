# Spec 003 — Entry-details codecs

Date: 2026-10-01

Status: Accepted (2026-10-01: Q1, Q2 and Q3 as recommended)

Depends on: ADR 0010 §3 (forward compatibility), §7 (the guards) and §8 (evolving stored data);
Spec 001 (the details types).

Delivered by: **Increment 3** (`docs/ROADMAP.md`).

## 1. Purpose

This spec turns each kind's details into JSON and back, in exactly one place: the
`Coo.Codecs` project, compiled once for the phone and the server. It covers:

- the JSON shape of each v1 kind;
- the `"v"` version field and the upcaster chain;
- the **write gate** (G2): nothing is written that does not decode back to the same value;
- the three decode outcomes: decoded, unrepresentable, or malformed;
- **preserving unknown properties** when an older app edits an entry;
- the tests that make all of this trustworthy (G6), and the first ADR 0010 completeness tests.

**Out of scope:** the core columns (they are columns, not JSON; ADR 0010 §2), the sync envelope
(the sync spec), tables and the read guard's quarantine (Increment 4), and server-side
downcasting (the sync spec).

## 2. Where it lives

| What | Project | Visibility |
|---|---|---|
| Details DTOs, one per kind and version | `Coo.Contracts/CareLog/Kinds/<Kind>/` | public (they are the wire format) |
| Codecs, the registry, the write gate, the merge, upcasters, the JSON context | `Coo.Codecs` | `IEntryDetailsCodec` and `EntryDetailsCodecs` are public. Everything else is `internal`. |
| Tests, fixtures, generators | `tests/Coo.Codecs.Tests` | — |

`System.Text.Json` is part of the shared framework, so `Coo.Contracts` and `Coo.Codecs` still
reference no package (ADR 0001 §2, enforced by the architecture tests).

## 3. JSON conventions

These apply to every kind, now and later:

| Rule | Detail |
|---|---|
| **Version first** | Every document is an object whose first property is `"v"`, a positive integer, per kind. |
| **camelCase** | Property names are camelCase. |
| **Enum values are strings** | Values are camelCase strings, for example `"left"` or `"expressedBreastMilk"`. A DTO holds enum values as `string`, never as a C# enum, so an unknown value reaches the codec instead of failing deserialization. The codec maps it, and reports an unknown value as **unrepresentable** (§5). |
| **Volumes** | A JSON number of millilitres, with at most one decimal place, under a key ending in `Ml` (for example `"leftMl"`). |
| **Instants** | ISO 8601 with the offset, for example `"2026-09-30T14:00:00+02:00"`. The offset is part of the data (ADR 0006 §5) and must survive a round trip. |
| **Absent, not null** | Optional values are omitted when empty. `null` is accepted on read and treated as absent. |
| **Strict types** | A string where a number belongs, a missing required property, or a non-object document is **malformed**. Numbers are never read from strings. |
| **Serialisation** | A source-generated `JsonSerializerContext`, so the trimmed Android build keeps working (ADR 0010 §4). The writer emits no indentation, and properties in declaration order, so the same value always encodes to the same bytes. |

## 4. The v1 documents

### 4.1 `feeding`, v1

The method is the discriminator. One flat DTO, `FeedingDetailsV1`, holds both shapes; the codec
checks that each method carries only its own properties.

```json
{ "v": 1, "method": "breast", "completed": true,
  "segments": [
    { "side": "left",  "start": "2026-09-30T14:00:00+02:00", "end": "2026-09-30T14:10:00+02:00" },
    { "side": "right", "start": "2026-09-30T14:10:00+02:00", "end": "2026-09-30T14:15:00+02:00" } ] }
```

```json
{ "v": 1, "method": "bottle",
  "components": [ { "milk": "expressedBreastMilk", "volumeMl": 60 }, { "milk": "formula", "volumeMl": 90 } ] }
```

| Property | Breast | Bottle |
|---|---|---|
| `method` | `"breast"` | `"bottle"` |
| `completed` | required boolean | must be absent |
| `segments` | required, 1 or more. `end` is absent on an open segment. | must be absent |
| `components` | must be absent | required, 1 or 2 |

### 4.2 `diaper`, v1

```json
{ "v": 1, "contents": "wet" }
```

`contents` is `"wet"`, `"dirty"` or `"mixed"`.

### 4.3 `sleep`, v1

```json
{ "v": 1 }
{ "v": 1, "kindOverride": "nap" }
```

`kindOverride` is `"nap"` or `"night"`, or absent (the classifier decides).

### 4.4 `pump`, v1

```json
{ "v": 1 }
{ "v": 1, "yield": { "leftMl": 70, "rightMl": 55 } }
{ "v": 1, "yield": { "totalMl": 125 } }
```

`yield` is absent (not recorded yet), **or** has at least one of `leftMl` and `rightMl`, **or** has
only `totalMl`. A total is never stored alongside sides: it is derived from them.

## 5. The codec

```csharp
public interface IEntryDetailsCodec
{
    EntryKindCode Kind { get; }
    int CurrentVersion { get; }

    /// Encodes through the write gate (§6). previous is the stored document being replaced, if any,
    /// whose unknown top-level properties are carried over (§7).
    string Encode(EntryDetails details, string? previous = null);

    DecodeOutcome Decode(string json);
}

public abstract record DecodeOutcome;
public sealed record Decoded(EntryDetails Details, int StoredVersion) : DecodeOutcome;
public sealed record Unrepresentable(string Reason) : DecodeOutcome;  // valid, but newer than this client
public sealed record Malformed(string Reason) : DecodeOutcome;        // broken: never valid in any version
```

`EntryDetailsCodecs` is a static, hand-written list, like `EntryKinds` (Spec 001 §7.1), with
`For(EntryKindCode)` and `For(EntryDetails)`.

**What each outcome means:**

| Outcome | When | The phone does | The server does |
|---|---|---|---|
| `Decoded` | The document reads, upcasts and passes the Domain factories | Uses it | Uses it |
| `Unrepresentable` | `v` is above `CurrentVersion`, or an enum string is unknown (`"donorMilk"`), or a method is unknown | Keeps the raw document and shows the entry **read-only**, with "update the app to edit" (ADR 0010 §3.3) | Should not happen, because the server is deployed first. It is answered as retryable, never as a final rejection, so no entry is lost to a deploy-order mistake. That rule belongs to the sync spec. |
| `Malformed` | Not JSON, not an object, `v` missing or below 1, a wrong type, a missing required property, properties of the wrong method, or a Domain rule fails | **Quarantine** (G4, Increment 4) | Rejects the change, with the reason |

`Reason` is a machine-readable code, for example `details.version-too-new`,
`details.unknown-value:milk`, `details.missing:segments`, or a Domain error code such as
`breast.segments-overlap`.

## 6. The write gate (G2)

`Encode` never returns a document that would not read back as the same value:

1. Map the details to the current DTO, and serialise it.
2. Merge back unknown properties from `previous` (§7).
3. **Decode** the result with the same codec.
4. Require `Decoded`, with details **equal** to the input.
5. Encode the decoded value again, and require the **same bytes** as step 1. This catches what
   value equality misses, for example a lost offset: two `DateTimeOffset`s for the same instant
   are equal even when their offsets differ.

If any step fails, `Encode` throws `DetailsEncodingException`. This is a programming error, never
a user error: a Domain object is valid by construction, so failing to round-trip it is a bug in a
codec. The persistence layer lets the exception roll back the transaction, so nothing is written.

## 7. Preserving unknown properties (ADR 0010 §3.2)

When an older app edits an entry, it must not erase the fields that a newer app wrote.

- On read, the phone stores the **raw document** next to the decoded value (Increment 4 stores it
  in the `details` column).
- On write, `Encode(details, previous: raw)` copies every **top-level** property of `previous`
  whose name is **not a property of the current DTO**, and that the new document does not
  already contain.
- **A known property is never resurrected.** If the person cleared an optional value, such as
  removing a sleep's nap label, the property is absent from the new document. It is not copied
  back from `previous`, because its name is known.
- **Only top-level properties are merged.** Nested objects (segments, bottle components, the
  yield) are closed: a property added *inside* them is not additive. It needs a new version, and
  older clients then see the document as unrepresentable, and read-only. Merging inside arrays by
  position would be guesswork, and guesswork is how data gets corrupted.
- **`previous` is upcast to the current version before merging.** Otherwise the properties that an
  older version used, and a newer one renamed, would look unknown and be carried over next to their
  replacements.
- The merged document still passes the write gate. The gate's equality check runs on the decoded
  value, which ignores unknown properties, so preserving them never makes the gate fail.

## 8. Versions and upcasters (ADR 0010 §8)

- Each codec declares `CurrentVersion` (1 for every kind now), and an ordered list of
  **upcasters**, each a pure function `JsonObject → JsonObject` from version *n* to *n + 1*.
- Decoding a version-*n* document applies the upcasters for *n*, *n + 1*, and so on, then reads the
  current DTO.
- Upcasters work on JSON objects, not on one C# DTO type per old version, so old versions do not
  have to be kept as types. Correctness comes from the golden fixtures instead (§9): every
  released version keeps its fixtures forever, and they must decode in every build.
- `CurrentVersion` is bumped only for a change in shape (§7 explains why a nested addition is one).
  An additive top-level field needs no bump.
- No kind has a v2 yet, so the upcaster machinery is tested with a **test-only kind** that has a
  v1 → v2 rename.

## 9. Tests (`tests/Coo.Codecs.Tests`)

New test package: `FsCheck.Xunit` 3.3.0, in test projects only.

**Golden fixtures**, at `Fixtures/<kind>/v<n>/<case>.json`. The expected domain value of each
fixture is built in the test code with the Domain factories, not read from another JSON file, so a
codec bug cannot hide in both the fixture and its expectation. There are at least these cases:

| Kind | Cases |
|---|---|
| `feeding` | Breast completed with two sides; breast running (open last segment); breast paused; bottle of formula; mixed bottle; a non-UTC offset |
| `diaper` | Each of the three contents |
| `sleep` | No override; a nap; night |
| `pump` | No yield; left only; both sides; a total |

**`fixtures-manifest.txt`** lists every fixture file. It may only grow: a test fails if a listed
fixture is missing (ADR 0010 G6, "kept forever").

**Tests:**

| Test | Proves |
|---|---|
| `Decode_EveryGoldenFixture_YieldsExpectedValue` | Old documents read correctly, forever |
| `Encode_EveryGoldenFixture_ReproducesTheFixtureBytes` | v1 output is stable |
| `RoundTrip_GeneratedDetails_DecodeToEqualValue` (FsCheck, per kind, 1,000 cases) | Encode, then decode, gives back the value |
| `RoundTrip_GeneratedDetails_PreservesOffsets` | Offsets survive the round trip |
| `Decode_Malformed…` (one per row of §5's malformed list, per kind where it applies) | Broken input never becomes a domain object |
| `Decode_UnknownEnumValue_ReturnsUnrepresentable` | `"donorMilk"`, `"lavender"` |
| `Decode_VersionAboveCurrent_ReturnsUnrepresentable` | A newer client's document |
| `Encode_PreviousWithUnknownProperty_KeepsIt` | ADR 0010 §3.2: an older app keeps the newer stool colour |
| `Encode_KnownPropertyClearedByEdit_IsNotResurrected` | Clearing the nap label works |
| `Encode_UnknownNestedProperty_IsNotMerged` | §7: nested objects are closed |
| `Encode_CodecThatLosesData_ThrowsDetailsEncodingException` | The gate fires (a deliberately broken test codec) |
| `Decode_TestKindV1_UpcastsToV2` | The upcaster chain |
| `Decode_SameVersionTwice_IsDeterministic` | No hidden state |

**Completeness tests** (the first of ADR 0010 §4). The two that need test assets (fixtures,
generators) live in `Coo.Codecs.Tests`; the others in `Coo.Architecture.Tests`:

| Test | Rule |
|---|---|
| `EveryKind_HasExactlyOneCodec` | `EntryKinds.All` and `EntryDetailsCodecs` agree |
| `EveryCodecVersion_HasGoldenFixtures` | Every version from 1 to `CurrentVersion` of every kind has at least one fixture |
| `EveryKind_HasRoundTripGenerator` | A new kind cannot skip the property tests |
| `Application_Types_DoNotDependOnSystemTextJson` | Only `Coo.Codecs`, `Coo.Contracts` and (from Increment 4) the persistence folders touch JSON |

## 10. Increment 3 — deliverables

| # | Deliverable | How you check it |
|---|---|---|
| 1 | Four codecs with the v1 shapes above | `dotnet test`: the golden-fixture tests pass |
| 2 | The write gate fires | The broken-codec test |
| 3 | Unknown properties survive an older app's edit, and cleared ones stay cleared | The merge tests |
| 4 | The upcaster chain works | The test-kind v1 → v2 test |
| 5 | Generated round trips pass | The FsCheck tests (1,000 cases per kind) |
| 6 | A new kind cannot skip a codec, fixtures or generators | The completeness tests. They are shown failing once, by temporarily adding a test-only kind to the catalogue. |
| 7 | CI is green | GitHub Actions |

## 11. Decisions for the owner

| # | Question | Recommendation |
|---|---|---|
| Q1 | Preserve unknown properties at the top level only, so a field added inside a segment or a component needs a new version? | **Yes.** Merging inside arrays by position is guesswork. |
| Q2 | Upcasters on JSON objects, kept correct by fixtures, instead of a C# type for every old version | **Yes.** The fixtures are the real guarantee either way, and old types are dead weight. ADR 0010 §8 said "from one DTO version to the next"; this refines it. |
| Q3 | Deploy order: the server is always deployed before a client that writes a new version, and a server that meets an unknown version answers "retry", not "rejected" | **Yes.** Recorded here, enforced in the sync spec. |
