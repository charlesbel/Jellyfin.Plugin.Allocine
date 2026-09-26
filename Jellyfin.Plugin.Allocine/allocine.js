(function () {
    "use strict";

    console.log("[Allocine] Script loaded");

    const CONFIG = {
        endpoint: "Allocine/Ratings",
        logoBase: "https://cdn.jsdelivr.net/gh/charlesbel/Jellyfin.Plugin.Allocine@master/img/",
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

    function getCurrentItemId() {
        const hash = window.location.hash || location.hash || "";
        const queryIndex = hash.indexOf("?");
        if (queryIndex < 0) return null;
        return new URLSearchParams(hash.slice(queryIndex + 1)).get("id");
    }


    function findRatingContainer() {
        const detailPage = document.querySelector("#itemDetailPage:not(.hide)");
        return detailPage ? detailPage.querySelector(".itemMiscInfo-primary") : null;
    }

    function removeRatings() {
        document.querySelectorAll(".allocine-ratings-group, .allocine-custom-rating").forEach(element => element.remove());
    }

    let requestGeneration = 0;
    let debounceTimer = null;
    const inFlightRequests = new Map();
    const failureRetryAfter = new Map();
    const failureRetryDelayMs = 30 * 1000;
    let lastRouteItemId = null;

    function scheduleRun(delay = 75) {
        if (debounceTimer) clearTimeout(debounceTimer);
        debounceTimer = setTimeout(runLogic, delay);
    }

    function requestBecameStale(generation, routeItemId, target) {
        const currentRouteItemId = getCurrentItemId();
        const currentTarget = findRatingContainer();
        const stale = generation !== requestGeneration
            || currentRouteItemId !== routeItemId
            || target.isConnected === false
            || target !== currentTarget;
        if (!stale) return false;

        if (generation === requestGeneration) {
            if (currentRouteItemId === routeItemId && currentTarget && currentTarget !== target) {
                scheduleRun(0);
            }
        }

        return true;
    }

    function finishFailedRequest(generation, routeItemId, target) {
        if (generation !== requestGeneration) return;

        const currentTarget = findRatingContainer();
        if (getCurrentItemId() === routeItemId && currentTarget && currentTarget !== target) {
            scheduleRun(0);
        }
    }

    async function runLogic() {
        const target = findRatingContainer();
        if (!target) return;

        const routeItemId = getCurrentItemId();
        if (!routeItemId) return;
        if (routeItemId !== lastRouteItemId) {
            lastRouteItemId = routeItemId;
            failureRetryAfter.delete(routeItemId);
        }

        const processedId = target.getAttribute("data-allocine-processed");
        if (processedId === routeItemId) {
            if (target.querySelector(".allocine-ratings-group")) {
                ensureRatingPlacement(target);
                return;
            }

            // Another Jellyfin extension can rebuild the media-info children while
            // preserving attributes on the container. A processed marker without
            // our group is stale, so revalidate through the server cache.
            target.removeAttribute("data-allocine-processed");
        }

        const retryAfter = failureRetryAfter.get(routeItemId);
        if (retryAfter && retryAfter > Date.now()) return;

        const generation = ++requestGeneration;

        removeRatings();
        target.removeAttribute("data-allocine-processed");

        const params = {
            itemId: routeItemId,
        };
        const url = ApiClient.getUrl(CONFIG.endpoint, params);

        try {
            const data = await getRatingsRequest(routeItemId, url);
            if (requestBecameStale(generation, routeItemId, target)) return;
            failureRetryAfter.delete(routeItemId);
            target.setAttribute("data-allocine-processed", routeItemId);
            injectRatings(target, data);
        } catch (error) {
            if (generation === requestGeneration) {
                if (getCurrentItemId() === routeItemId && findRatingContainer() === target) {
                    failureRetryAfter.set(routeItemId, Date.now() + failureRetryDelayMs);
                }
                finishFailedRequest(generation, routeItemId, target);
                target.removeAttribute("data-allocine-processed");
                removeRatings();
            }
            console.error("[Allocine] Fetch error:", error);
        }
    }

    function getRatingsRequest(itemId, url) {
        const existing = inFlightRequests.get(itemId);
        if (existing) return existing;

        const request = Promise.resolve()
            .then(() => ApiClient.getJSON(url))
            .finally(() => {
                if (inFlightRequests.get(itemId) === request) {
                    inFlightRequests.delete(itemId);
                }
            });
        inFlightRequests.set(itemId, request);
        return request;
    }

    function injectRatings(targetElement, data) {
        removeRatings();

        const group = document.createElement("span");
        group.className = "allocine-ratings-group";
        const ratingsToShow = [];
        if (data.presse) ratingsToShow.push({ key: "presse", value: data.presse });
        if (data.public) ratingsToShow.push({ key: "spectateur", value: data.public });

        ratingsToShow.forEach(rating => {
            const element = document.createElement("div");
            element.className = "mediaInfoItem mediaInfoCriticRating allocine-custom-rating";
            element.textContent = `${formatRating(rating.value)}/5`;
            Object.assign(element.style, {
                display: "flex",
                alignItems: "center",
                marginRight: "1em",
                fontWeight: "500",
                backgroundImage: `url(${CONFIG.logoBase}${CONFIG.logos[rating.key]})`,
                backgroundRepeat: "no-repeat",
                backgroundSize: "auto 1.2em",
                backgroundPosition: "left center",
                paddingLeft: "1.6em",
            });
            group.appendChild(element);
        });

        if (isEditorialPresent(data.classiques)) {
            group.appendChild(createEditorialBadge("classiques", "Classiques AlloCiné"));
        }
        if (isEditorialPresent(data.clubAime)) {
            group.appendChild(createEditorialBadge("club-aime", "Le club Aime"));
        }
        if (isEditorialPresent(data.lesIndes)) {
            group.appendChild(createEditorialBadge("les-indes", "Les Indés"));
        }
        if (isEditorialPresent(data.clubScream)) {
            group.appendChild(createEditorialBadge("club-scream", "Club Scream"));
        }

        if (group.children.length > 0) {
            targetElement.appendChild(group);
            ensureRatingPlacement(targetElement);
        }
    }

    function isEditorialPresent(value) {
        return value === "1" || value === 1 || value === true || value === "true";
    }

    function badgeUrl(name) {
        const script = document.querySelector("script[src*=\"Allocine/Script\"]");
        const src = script && script.src;
        if (src) {
            return src.replace(/Script(\?.*)?$/, "Badge/" + name);
        }
        if (typeof ApiClient !== "undefined" && typeof ApiClient.getUrl === "function") {
            return ApiClient.getUrl("Allocine/Badge/" + name);
        }
        return "";
    }

    function createEditorialBadge(name, label) {
        const element = document.createElement("div");
        element.className = "mediaInfoItem allocine-custom-rating allocine-editorial-badge";
        element.title = label;
        if (typeof element.setAttribute === "function") {
            element.setAttribute("aria-label", label);
        }
        Object.assign(element.style, {
            display: "inline-flex",
            alignItems: "center",
            marginRight: "1em",
            height: "1.25em",
        });
        const img = document.createElement("img");
        img.src = badgeUrl(name);
        img.alt = label;
        Object.assign(img.style, {
            display: "block",
            height: "1.25em",
            width: "auto",
        });
        element.appendChild(img);
        return element;
    }

    function findDirectChild(targetElement, selector) {
        const candidates = typeof targetElement.querySelectorAll === "function"
            ? targetElement.querySelectorAll(selector)
            : [targetElement.querySelector(selector)];
        return Array.from(candidates).find(candidate => candidate?.parentNode === targetElement) ?? null;
    }

    function ensureRatingPlacement(targetElement) {
        const group = targetElement.querySelector(".allocine-ratings-group");
        if (!group) return;

        const firstNativeRating = findDirectChild(targetElement,
            ".starRatingContainer, .mediaInfoCriticRating:not(.allocine-custom-rating), .mediaInfoCommunityRating:not(.allocine-custom-rating)");
        if (firstNativeRating && group.nextSibling !== firstNativeRating) {
            targetElement.insertBefore(group, firstNativeRating);
            return;
        }

        // Some items have no native Jellyfin rating. In that case, appending the
        // group puts it after metadata added by Jellyfin Enhanced and makes its
        // visual position vary between items. Anchor it directly after the
        // parental classification instead.
        const officialRating = findDirectChild(targetElement, ".mediaInfoOfficialRating");
        if (!firstNativeRating && officialRating && officialRating.nextSibling !== group) {
            if (officialRating.nextSibling) {
                targetElement.insertBefore(group, officialRating.nextSibling);
            } else {
                targetElement.appendChild(group);
            }
        }
    }

    const observer = new MutationObserver(mutations => {
        if (!getCurrentItemId()) return;
        const relevantMutation = mutations.some(mutation => {
            if (mutation.target.classList?.contains("allocine-custom-rating")) return false;
            return mutation.attributeName !== "data-allocine-processed";
        });
        if (!relevantMutation) return;

        scheduleRun();
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
        style.textContent = ".allocine-ratings-group { display: contents; } .allocine-custom-rating::after { content: none !important; display: none !important; }";
        document.head.appendChild(style);
    }

    runLogic();
})();
