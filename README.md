# Jellyfin Allociné Ratings Plugin

<p align="center">
  <img src="https://img.shields.io/badge/jellyfin-12.0-blue?style=for-the-badge&logo=jellyfin" alt="Jellyfin Version">
  <img src="https://img.shields.io/badge/.NET-10.0-purple?style=for-the-badge&logo=dotnet" alt=".NET Version">
  <img src="https://img.shields.io/github/license/charlesbel/Jellyfin.Plugin.Allocine?style=for-the-badge" alt="License">
  <img src="https://img.shields.io/github/downloads/charlesbel/Jellyfin.Plugin.Allocine/total?style=for-the-badge" alt="Downloads">
  <img src="https://img.shields.io/jsdelivr/gh/hm/charlesbel/Jellyfin.Plugin.Allocine?style=for-the-badge&logo=jsdelivr" alt="jsDelivr Hits">
</p>

<p align="center">
  <img src="https://cdn.jsdelivr.net/gh/charlesbel/Jellyfin.Plugin.Allocine@master/img/allocine_crit.png?" width="100" />
  <img src="https://cdn.jsdelivr.net/gh/charlesbel/Jellyfin.Plugin.Allocine@master/img/allocine_user.png?" width="100" />
</p>

<p align="center">
  <strong>Seamlessly integrate French Allociné ratings into your Jellyfin instance.</strong>
</p>

---

## 📖 Overview

The **Jellyfin Allociné Plugin** fetches and displays movie and series ratings from **Allociné** directly on Jellyfin detail pages.

Unlike standard metadata providers, this plugin injects the specific **"Presse" (Critics)** and **"Spectateurs" (Audience)** scores alongside standard ratings, using the official Allociné visual style. It is designed for French-speaking users who rely on these specific metrics to choose their next movie.

### ✨ Key Features

-   **Native AlloCiné Identity:** Adds an AlloCiné ID field and a clickable AlloCiné link on movies and series, without becoming a full remote metadata fetcher.
-   **Exact Writes Only:** Optionally writes `ProviderIds["Allocine"]` after an exact IMDb or TMDb match. Title/year fallback remains display-only.
-   **Dual Ratings:** Displays both _Press_ and _Spectator_ scores from a private plugin database. These scores never overwrite Jellyfin's Community, Critic, Custom, or Official ratings.
-   **Native Look & Feel:** Uses official Allociné icons and specific French number formatting (e.g., `3,5/5`).
-   **Exact Identity Matching:** Resolves Jellyfin IMDb/TMDb identifiers through Wikidata and refuses ambiguous or contradictory mappings.
-   **Movies and Series:** Supports both Jellyfin movie and series detail pages.
-   **Resilient Mobile Authentication:** Reproduces the anonymous authentication flow used by the current Allociné Android application and renews it automatically when rejected.
-   **Safe Public Fallback:** Falls back to the public movie page if the mobile API is unavailable, while refusing Cloudflare challenge or malformed pages.
-   **Private Persistent Cache:** Stores mappings and ratings in a plugin-owned SQLite database without modifying Jellyfin's databases or native rating fields.
-   **Adaptive Refresh:** A scheduled task refreshes volatile new releases more frequently, applies retry backoff, preserves the last known rating during remote failures, and keeps plugin-owned AlloCiné IDs aligned with the private mapping (including a 180-day re-check).
-   **Resilient Injection:** Handles single-page navigation and late DOM rebuilding while keeping AlloCiné badges between parental classification and Jellyfin's native ratings.
-   **Cache-First Display:** Serves cached ratings immediately and performs a dynamic lookup only when needed.

---

## 🖼️ Screenshots

|            Before             |         After          |
| :---------------------------: | :--------------------: |
| _Standard Jellyfin Interface_ | _With Allociné Plugin_ |
|  ![Before](img/without.png)   | ![After](img/with.png) |

> _Note: The ratings appear next to the official certification or duration on the movie details page._

---

## ⚙️ How It Works (Technical Insight)

This plugin utilizes a hybrid approach combining a C# backend controller and a JavaScript frontend injection.

### 1. Exact Media Identity and GraphQL Ratings

The backend reads the current media item from Jellyfin and uses its IMDb and TMDb identifiers to resolve the corresponding Allociné movie or series identifier through Wikidata. When both identifiers are present, they must resolve consistently. Ambiguous, missing, or contradictory mappings are rejected rather than guessed. A title/year lookup is reserved for an interactive cache miss and still requires an exact, unambiguous result.

A post-refresh custom metadata provider can persist a proven AlloCiné identifier as `ProviderIds["Allocine"]` and expose the matching public page as a native external link. The daily scheduled task uses the same exact-write path so a mapping change after 180 days updates both the private database and Jellyfin. That write is optional, never uses a title/year fallback, never overwrites an ID typed by the user, and never copies AlloCiné scores into Jellyfin rating fields.

Once identity is established, the plugin retrieves the Press and Audience ratings from the **GraphQL API** used by the official Allociné Android application.

### 2. Anonymous Mobile Authentication

The current mobile API requires an additional FCM registration token. The plugin creates an anonymous Android Check-in identity, registers it for the official Allociné application through Google's `register3` endpoint, and sends the resulting token as `AC-Auth-Token`.

No Allociné account or user-provided API key is required. Generated device credentials and tokens are kept **in memory only**, never written to disk or logs. The token is cached for the plugin process and renewed once if GraphQL rejects it.

### 3. Fail-Closed Public Page Fallback

If anonymous registration or GraphQL is unavailable, the backend can extract the rounded Press and Audience ratings from the public Allociné movie page. The parser rejects Cloudflare challenges, incomplete pages, and ambiguous rating blocks instead of returning potentially incorrect values. It does not attempt to bypass anti-bot challenges.

### 4. Native Jellyfin 12 DOM Injection

The plugin serves a custom JavaScript file (`allocine.js`) and injects it into Jellyfin Web through the Jellyfin 12 ASP.NET request pipeline. No companion transformation plugin or modification of Jellyfin's files is required. The script observes detail-page navigation, calls an authenticated endpoint using only the Jellyfin item ID, and inserts its own badge group without rewriting Jellyfin or third-party extension nodes.

### 5. Persistent Cache and Scheduled Refresh

Ratings and validated IMDb/TMDb-to-Allociné mappings are stored separately in a private SQLite database under Jellyfin's plugin configuration directory. Mapping entries have a long lifetime, while rating freshness varies with the media's age and type. Expired positive ratings remain visible while a refresh runs. Failed attempts use bounded backoff and never erase a previously valid rating.

The built-in Jellyfin scheduled task warms only movies and series with stable IMDb or TMDb identifiers. Wikidata requests are serialized and paced, `Retry-After` is honored for rate limits, and transport failures are not stored as authoritative identity misses.

---

## ⚠️ Prerequisites

This release requires **Jellyfin Server 12.0.x**. Older plugin releases remain available for Jellyfin 10.x. No companion plugin is required.

---

## 🚀 Installation

### Option 1: Automatic Install (Repository)

1.  Open your Jellyfin Dashboard.
2.  Navigate to **Plugins** > **Repositories**.
3.  Add the following repository URL:
    ```
    https://raw.githubusercontent.com/charlesbel/Jellyfin.Plugin.Allocine/master/manifest.json
    ```
4.  Go to the **Catalog**, find **Allocine Ratings**, and install it.
5.  **Restart your Jellyfin server.**

### Option 2: Manual Install

1.  Download the latest `.dll` from the [Releases Page](https://github.com/charlesbel/Jellyfin.Plugin.Allocine/releases).
2.  Place the `Jellyfin.Plugin.Allocine.dll` file into your Jellyfin plugins folder:
    -   **Linux:** `/var/lib/jellyfin/plugins/Allocine/`
    -   **Windows:** `C:\ProgramData\Jellyfin\Server\plugins\Allocine\`
    -   **Docker:** `/config/plugins/Allocine/`
3.  **Restart your Jellyfin server.**

---

## 🛠️ Build from Source

If you want to contribute or build the plugin yourself:

1.  **Clone the repository:**

    ```bash
    git clone https://github.com/charlesbel/Jellyfin.Plugin.Allocine.git
    cd Jellyfin.Plugin.Allocine
    ```

2.  **Build the project:**

    ```bash
    dotnet publish Jellyfin.Plugin.Allocine/Jellyfin.Plugin.Allocine.csproj --configuration Release
    ```

3.  **Copy artifacts:**
    Copy the contents of `Jellyfin.Plugin.Allocine/bin/Release/net10.0/publish` to your Jellyfin plugins directory.

---

## 🤝 Contributing

Contributions are welcome! Please follow these steps:

1.  Fork the repository.
2.  Create your feature branch (`git checkout -b feature/AmazingFeature`).
3.  Commit your changes (`git commit -m 'Add some AmazingFeature'`).
4.  Push to the branch (`git push origin feature/AmazingFeature`).
5.  Open a Pull Request.

---

## ⚖️ License

Distributed under the **GNU General Public License v3.0**. See `LICENSE` for more information.

---

## 📝 Disclaimer

This plugin is not officially affiliated with, associated with, authorized by, endorsed by, or in any way officially connected with **Allociné** or **The Webedia Group**. All product and company names are trademarks™ or registered® trademarks of their respective holders. Use of them does not imply any affiliation with or endorsement by them.
