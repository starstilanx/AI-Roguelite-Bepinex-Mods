# AIROG_VertexAI

Adds **Google Vertex AI** as both a text-generation and an image-generation backend for
AI Roguelite, authenticated with a single **express-mode API key** — no Google Cloud
project, region, or service account needed.

## Setup

1. Get an express-mode API key from the Vertex AI console (Vertex AI → express mode).
2. Drop `AIROG_VertexAI.dll` in `BepInEx/plugins/` (it also needs `AIROG_Core.dll`).
3. **Text:** Options → Text Generation → **Vertex AI (Google)**, paste the key, pick a model.
4. **Images:** Options → Image Generation → **Vertex AI (Gemini Image)**, same key, pick a model.

The key is shared between the two — enter it in either row and the other mirrors it.

## Endpoint

Express mode uses the global endpoint with no project or location in the path:

```
POST https://aiplatform.googleapis.com/v1/publishers/google/models/{model}:generateContent
x-goog-api-key: <your key>
```

The key travels as a header rather than the documented `?key=` query parameter, so it
never appears in the game's URL logging.

## Model lists

Google churns model IDs fast, so the dropdowns are driven by a JSON file you can edit
instead of being baked into the DLL. On first run the mod writes:

```
BepInEx/config/airog_vertexai_models.json
```

Edit `textModels` / `imageModels` to add, remove, or reorder entries; restart the game to
pick up changes. Deleting the file restores the built-in defaults.

```jsonc
{
  "apiVersion": "v1",           // "v1beta1" for some preview models
  "safetyThreshold": "OFF",     // per-category safetySettings; "BLOCK_NONE" on older
                                //   models, "" to omit and use account defaults
  "maxConcurrentRequests": 1,   // matches the game's own OpenAI-API path
  "extraGenerationConfig": null,// merged into generationConfig on every text request
  "textModels": [
    { "label": "Gemini 3.7 Flash", "id": "gemini-3.7-flash", "thinking": null }
  ],
  "imageModels": [
    { "label": "Gemini 3 Pro Image · 2K", "id": "gemini-3-pro-image", "size": "2K" }
  ]
}
```

### `thinking`

Controls reasoning spend, which is billed against `maxOutputTokens`:

| Value | Emits | Use for |
|---|---|---|
| `"minimal"` / `"low"` / `"medium"` / `"high"` | `thinkingConfig.thinkingLevel` | Gemini 3.x |
| a number, e.g. `"0"` | `thinkingConfig.thinkingBudget` | Gemini 2.5 |
| `"default"` or `""` | nothing | leave it to the model |
| `null` (default) | auto — `low` for `gemini-3*`, `0` for `gemini-2.5*` | most cases |

When reasoning is on, the mod adds 1024 tokens of headroom so short prompts (the game
budgets as little as 16 output tokens for some checks) still return text.

**Which levels a model accepts is not predictable from its name.** Vertex rejects
`minimal` on `gemini-3.7-flash` with `400 Thinking level is unsupported:
THINKING_LEVEL_MINIMAL`, even though Flash models are documented to support all four
levels; Pro models don't take `minimal` at all. So the mod treats the setting as a
preference, not a promise: on that specific 400 it steps down the ladder
(`minimal` → `low` → send no `thinkingConfig`) and caches whichever works for the rest of
the session, one probe per model. Set `thinking` explicitly to skip the probe.

### `size`

`imageConfig.imageSize` — `"512"`, `"1K"`, `"2K"`, `"4K"`, or `""` for the model default
(1K). Aspect ratio is not configured here: the mod reuses whatever ratio the game already
computes for each entity and snaps it to the nearest value Gemini accepts.

> **Imagen is not offered.** Every `imagen-*` model is deprecated and shutting down from
> 2026-08-17; Google's guidance is to generate images with the Gemini image models.

## How it hooks in

**Text** rides on the game's existing `OPENAI_API` mode. The injected dropdown row reports
itself as `OPENAI_API`, which keeps every mode-keyed lookup in the game working —
`SS.I.summaryceptionByMode`, `Utils.GetFullPromptStrCharLimit`, `IsOfficialServers`. A
genuinely new enum value would throw `KeyNotFoundException` the first time the game sized
a prompt. `PREF_KEY_VERTEX_TEXT_ACTIVE` is what distinguishes "Vertex" from "some
OpenAI-compatible server", and a prefix on `OpenaiApiClient.GetGeneratedTextChatgpt`
redirects the call. The key and model rows are **clones** of existing Options rows, so
they never write to the stock OpenAI-compatible settings.

**Images** use `SS.ImageGenerationMode` value **98**, deliberately clear of
[AIROG_NanoBanana](../AIROG_NanoBanana)'s 99 — both mods can be installed and you pick one
in Options. Because 98 has no branch in the game's mode switches, several helpers
(`GetSettingsPojoByInd`, `PopulateImageGenPresetDropdown`, ...) are short-circuited, and
the image-gen preset dropdown is repurposed as the model picker.

The game's per-prompt-type temperature and token budgets are copied verbatim from
`OpenaiApiClient`, so switching to Vertex doesn't silently change how verbose or how
deterministic each kind of generation is.

Sprites get a flat-background prompt hint plus an ffmpeg colour-key pass (Gemini cannot
emit an alpha channel); the pass no-ops if the corners are already transparent or ffmpeg
is missing.

## PlayerPrefs

| Key | Meaning |
|---|---|
| `PREF_KEY_VERTEX_API_KEY` | Shared express-mode API key |
| `PREF_KEY_VERTEX_TEXT_MODEL` | Selected text model ID |
| `PREF_KEY_VERTEX_TEXT_ACTIVE` | `1` when Vertex services text generation |
| `PREF_KEY_VERTEX_IMG_MODEL` | Selected image model ID |
| `PREF_KEY_VERTEX_IMG_SIZE` | `imageConfig.imageSize` for that model |
| `PREF_KEY_VERTEX_IMG_ACTIVE` | `1` when Vertex services image generation |

## Known limits

- **Multimodal prompts** (`HackyTxtGenType.MULTI_MODAL_INPUT`) still route to the official
  AI Roguelite servers. The game only sends those to official backends, and that path is
  untouched.
- Event checks follow the main model, as they do for any custom backend.
- Streaming is not used; the game's pipeline is request/response.

## Status

Compiles clean against the 2026-08-12 game build. **Not yet play-tested.**
