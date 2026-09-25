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
  <strong>Les notes Presse et Spectateurs d'AlloCiné, directement sur vos fiches Jellyfin.</strong>
</p>

---

## Présentation

Le plugin affiche les notes **Presse** et **Spectateurs** d'AlloCiné sur les pages films et séries, avec les icônes officielles (par exemple `3,5/5`).

Il peut aussi ajouter un identifiant AlloCiné et un lien vers la fiche. Vous pouvez le taper vous-même, ou laisser le plugin le remplir à partir d'IMDb / TMDb. Un identifiant que vous avez saisi n'est jamais écrasé.

Les notes AlloCiné restent des badges à part : elles ne remplacent pas les notes Jellyfin, Rotten Tomatoes ou autres.

Ce plugin nécessite **Jellyfin Server 12**. Les anciennes versions restent disponibles pour Jellyfin 10.

---

## Captures d'écran

|            Avant             |         Après          |
| :---------------------------: | :--------------------: |
| _Interface Jellyfin standard_ | _Avec le plugin Allociné_ |
|  ![Before](img/without.png)   | ![After](img/with.png) |

> Les notes apparaissent à côté de la classification ou de la durée, sur la fiche du film ou de la série.

---

## Installation

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
    -   **Windows:** `C:\\ProgramData\\Jellyfin\\Server\\plugins\\Allocine\\`
    -   **Docker:** `/config/plugins/Allocine/`
3.  **Restart your Jellyfin server.**

---

## Build from Source

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

## Contributing

Contributions are welcome! Please follow these steps:

1.  Fork the repository.
2.  Create your feature branch (`git checkout -b feature/AmazingFeature`).
3.  Commit your changes (`git commit -m 'Add some AmazingFeature'`).
4.  Push to the branch (`git push origin feature/AmazingFeature`).
5.  Open a Pull Request.

---

## License

Distributed under the **GNU General Public License v3.0**. See `LICENSE` for more information.

---

## Disclaimer

This plugin is not officially affiliated with, associated with, authorized by, endorsed by, or in any way officially connected with **Allociné** or **The Webedia Group**. All product and company names are trademarks™ or registered® trademarks of their respective holders. Use of them does not imply any affiliation with or endorsement by them.
