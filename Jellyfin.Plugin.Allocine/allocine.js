(function () {
    console.log("[Allocine] Script loaded - Robust Mode");

    const CONFIG = {
        apiBase: "/Allocine/Ratings",
        logoBase: "https://cdn.jsdelivr.net/gh/charlesbel/Jellyfin.Plugin.Allocine@main/img/",
        logos: {
            presse: "allocine_crit.png",
            spectateur: "allocine_user.png",
        },
    };

    function formatRating(value) {
        const num = parseFloat(value);
        if (isNaN(num)) return value;
        return num.toFixed(1).replace(".", ",");
    }

    function getMovieInfo() {
        const detailPage = document.querySelector("#itemDetailPage:not(.hide)");
        if (!detailPage) return null;

        const titleEl = detailPage.querySelector("h1.itemName");
        if (!titleEl) return null;
        const title = titleEl.textContent.trim();

        const infoContainer = detailPage.querySelector(".itemMiscInfo-primary");
        if (!infoContainer) return null;

        const yearEl = infoContainer.querySelector(".mediaInfoItem");
        if (!yearEl) return null;
        const year = yearEl.textContent.trim();

        if (!title || !year || !/^\d{4}$/.test(year)) return null;

        return {
            title,
            year,
            id: `${title}_${year}`.replace(/\s+/g, ""),
        };
    }

    function findRatingTarget() {
        const detailPage = document.querySelector("#itemDetailPage:not(.hide)");
        if (!detailPage) return null;

        let target = detailPage.querySelector("div.mediaInfoItem.mediaInfoText.mediaInfoOfficialRating");

        if (!target) {
            const infoContainer = detailPage.querySelector(".itemMiscInfo-primary");
            if (infoContainer) {
                const items = infoContainer.querySelectorAll("div.mediaInfoItem");
                for (let el of items) {
                    if (/^\d+\s*(?:h|min)/i.test(el.textContent.trim())) {
                        target = el;
                        break;
                    }
                }
            }
        }
        return target;
    }

    function hasValidExternalLinks() {
        const linksContainer = document.querySelector(".itemExternalLinks");
        if (!linksContainer) return false;
        const html = linksContainer.innerHTML.toLowerCase();
        return html.includes("imdb.com") || html.includes("themoviedb.org") || html.includes("trakt.tv");
    }

    async function runLogic() {
        const info = getMovieInfo();
        if (!info) return;

        const target = findRatingTarget();
        if (!target) return;

        const currentAttr = target.getAttribute("data-allocine-processed");

        if (currentAttr === info.id) {
            return;
        }

        if (currentAttr && currentAttr !== info.id) {
            document.querySelectorAll(".allocine-custom-rating").forEach((el) => el.remove());
            target.removeAttribute("data-allocine-processed");
        }

        if (!hasValidExternalLinks()) {
            return;
        }

        target.setAttribute("data-allocine-processed", info.id);

        console.log(`[Allocine] New movie detected: ${info.title} (${info.year})`);

        try {
            const response = await fetch(`${CONFIG.apiBase}?title=${encodeURIComponent(info.title)}&year=${info.year}`);

            if (!response.ok) {
                console.log("[Allocine] API Error or no results.");
                return;
            }

            const data = await response.json();
            injectRatings(target, data);
        } catch (e) {
            console.error("[Allocine] Fetch error:", e);
            target.removeAttribute("data-allocine-processed");
        }
    }

    function injectRatings(targetElement, data) {
        document.querySelectorAll(".allocine-custom-rating").forEach((el) => el.remove());

        const ratingsToShow = [];
        if (data.presse) ratingsToShow.push({ key: "presse", value: data.presse });
        if (data.public) ratingsToShow.push({ key: "spectateur", value: data.public });

        ratingsToShow.forEach((r) => {
            const div = document.createElement("div");
            div.className = "mediaInfoItem mediaInfoCriticRating allocine-custom-rating";
            div.textContent = `${formatRating(r.value)}/5`;

            Object.assign(div.style, {
                display: "flex",
                alignItems: "center",
                marginRight: "1em",
                fontWeight: "500",
                backgroundImage: `url(${CONFIG.logoBase}${CONFIG.logos[r.key]})`,
                backgroundRepeat: "no-repeat",
                backgroundSize: "auto 1.2em",
                backgroundPosition: "left center",
                paddingLeft: "1.6em",
            });

            targetElement.insertAdjacentElement("afterend", div);
        });
    }

    let debounceTimer = null;
    const observer = new MutationObserver((mutations) => {
        let relevantMutation = false;
        for (const m of mutations) {
            if (m.target.classList && m.target.classList.contains("allocine-custom-rating")) continue;
            if (m.attributeName === "data-allocine-processed") continue;

            relevantMutation = true;
            break;
        }

        if (!relevantMutation) return;

        if (debounceTimer) clearTimeout(debounceTimer);
        debounceTimer = setTimeout(() => {
            runLogic();
        }, 50);
    });

    observer.observe(document.body, {
        childList: true,
        subtree: true,
        attributes: true,
        attributeFilter: ["class", "src"],
    });

    if (!document.getElementById("allocine-style-fix")) {
        const style = document.createElement("style");
        style.id = "allocine-style-fix";
        style.textContent = `.allocine-custom-rating::after { content: none !important; display: none !important; }`;
        document.head.appendChild(style);
    }

    runLogic();
})();
