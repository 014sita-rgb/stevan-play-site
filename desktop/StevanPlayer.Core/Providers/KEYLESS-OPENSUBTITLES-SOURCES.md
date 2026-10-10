# Public OpenSubtitles integration evidence

Inspected 2026-10-10. This file records source evidence and validation limits;
it does not certify that a network service or Windows subtitle rendering passed.

The provider uses the **public official OpenSubtitles v3 addon**, rather than
claiming anonymous access to OpenSubtitles REST or using somebody else's API key.

- Official addon registration: <https://github.com/Stremio/stremio-official-addons/blob/master/index.json>.
  Its `org.stremio.opensubtitlesv3` descriptor advertises
  `https://opensubtitles-v3.strem.io/manifest.json`, resources `subtitles`,
  movie and series types, and IMDb `tt` prefixes. It does not advertise
  required user configuration or an API key. The same file registers official
  Cinemeta at `https://v3-cinemeta.strem.io/manifest.json`.
- Official protocol documentation:
  <https://github.com/Stremio/stremio-addon-sdk/blob/master/docs/protocol.md>.
  It describes a universal interface for "Stremio or other similar applications",
  HTTP resource requests, and public JSON responses.
- Subtitle request documentation:
  <https://github.com/Stremio/stremio-addon-sdk/blob/master/docs/api/requests/defineSubtitlesHandler.md>.
  It defines `type`, video `id`, and optional `filename`, `videoHash`, and `videoSize`.
- Subtitle response documentation:
  <https://github.com/Stremio/stremio-addon-sdk/blob/master/docs/api/responses/subtitles.md>.
  It defines unique `id`, subtitle `url`, language `lang`, and optional `label`.
- Metadata documentation:
  <https://github.com/Stremio/stremio-addon-sdk/blob/master/docs/api/responses/meta.md>.
  It defines title, type, release year, and series video IDs such as `tt0108778:1:1`.

Public GitHub repositories were read through normal authenticated-platform git
transport; no integration secret, demonstration key, browser cookie, or password
was extracted. The SDK and official addon registry publish MIT licenses.

Live probes to the addon and Stremio documentation hosts failed at the cloud
network's CONNECT stage with HTTP 403, including an approved unsandboxed retry.
The origin was not reached. This establishes an unverified network route,
not an origin rule prohibiting this integration. The 24 initial provider tests
use documented protocol fixtures; they are **not live subtitle searches**.

The implementation sends its real application user agent `StevanPlayer/1.0`,
serializes requests at most once per second, respects quota errors with a
one-minute retry pause, bounds results and payloads, and accepts only explicit
HTTPS download hosts. Downloads are URLs delegated by the public addon protocol;
the provider does not scrape website download pages.

Other investigated routes are not substituted silently:

- Official OpenSubtitles REST getting-started documentation says every request
  requires an application API key:
  <https://opensubtitles.tawk.help/article/getting-started>.
  "Anonymous" download quotas mean no user login, not no application key.
- Official legacy Kodi documentation says the OpenSubtitles.org API requires
  a VIP account as of January 2024:
  <https://opensubtitles.tawk.help/article/kodi>.
  Historical anonymous XML-RPC examples therefore do not establish current access.
- SubDL describes allowed server-rendered catalog/search pages in
  <https://subdl.com/llms-full.txt>. A public Charade search and its 1963 title
  page returned actual metadata. However, <https://dl.subdl.com/robots.txt>
  explicitly returns `User-agent: *` / `Disallow: /`, and the website robots
  disallows archive downloads. No CDN subtitle archive was fetched through
  that keyless website route. The official SubDL Stremio configuration
  <https://subdl.com/stremio> explicitly requires a user's API key.

Release gates still require real Windows playback, the actual public metadata
and subtitle responses, downloading a selected sr/hr/bs subtitle, and visible
caption rendering with the selected film. A service change or a download host
outside the reviewed whitelist must produce a reported provider failure.
