import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import test from 'node:test';
import vm from 'node:vm';

const scriptPath = new URL('../Jellyfin.Plugin.Allocine/allocine.js', import.meta.url);
const source = await readFile(scriptPath, 'utf8');

function createHarness(item) {
    const badges = [];
    const nativeRating = { id: 'native-rating' };
    const visualOrder = [nativeRating];
    const attributes = new Map();
    let ratingGroup = null;
    const infoContainer = {
        appendChild(element) {
            ratingGroup = element;
            badges.push(...(element.children ?? []));
            visualOrder.push(...(element.children ?? []));
        },
        getAttribute(name) {
            return attributes.get(name) ?? null;
        },
        insertBefore(element, reference) {
            ratingGroup = element;
            const children = element.children ?? [];
            children.forEach(child => {
                if (!badges.includes(child)) badges.push(child);
                const oldIndex = visualOrder.indexOf(child);
                if (oldIndex >= 0) visualOrder.splice(oldIndex, 1);
            });
            visualOrder.splice(visualOrder.indexOf(reference), 0, ...children);
        },
        querySelector(selector) {
            if (selector === '.allocine-ratings-group') return ratingGroup;
            return selector.includes('mediaInfoCriticRating') ? nativeRating : null;
        },
        querySelectorAll(selector) {
            return selector.includes('mediaInfoCriticRating') ? [nativeRating] : [];
        },
        removeAttribute(name) {
            attributes.delete(name);
        },
        setAttribute(name, value) {
            attributes.set(name, value);
        },
    };
    nativeRating.parentNode = infoContainer;
    const detailPage = {
        querySelector(selector) {
            if (selector === 'h1.itemName') return { textContent: item.Name };
            if (selector === '.itemMiscInfo-primary') return infoContainer;
            return null;
        },
    };
    let apiParams = null;
    let getJsonCalls = 0;
    const apiClient = {
        getCurrentUserId: () => 'user-id',
        getItem: async (userId, itemId) => {
            assert.equal(userId, 'user-id');
            assert.equal(itemId, item.Id);
            return item;
        },
        getUrl: (path, params) => {
            assert.equal(path, 'Allocine/Ratings');
            apiParams = params;
            return 'http://jellyfin.test/Allocine/Ratings';
        },
        getJSON: async () => {
            getJsonCalls += 1;
            return { presse: '3.8', public: '4.4' };
        },
    };
    const document = {
        body: {},
        head: { appendChild() {} },
        createElement: () => ({ style: {}, children: [], appendChild(child) { this.children.push(child); } }),
        getElementById: () => ({}),
        querySelector: selector => selector === '#itemDetailPage:not(.hide)' ? detailPage : null,
        querySelectorAll: () => [],
    };
    const context = {
        ApiClient: apiClient,
        MutationObserver: class { observe() {} },
        clearTimeout,
        console,
        document,
        fetch: async () => ({ ok: true, json: async () => ({ presse: '3.8', public: '4.4' }) }),
        isNaN,
        location: { hash: `#/details?id=${item.Id}`, href: `http://jellyfin.test/web/#/details?id=${item.Id}` },
        parseFloat,
        setTimeout,
        URLSearchParams,
        window: { ApiClient: apiClient, location: { hash: `#/details?id=${item.Id}`, href: `http://jellyfin.test/web/#/details?id=${item.Id}` } },
    };
    context.window.window = context.window;
    vm.runInNewContext(source, context);
    return {
        badges,
        getApiParams: () => apiParams,
        getJsonCalls: () => getJsonCalls,
        visualOrder,
    };
}

test('series item ID is sent to the authenticated plugin endpoint', async () => {
    const harness = createHarness({
        Id: '1eaff7b6-c537-d877-c83d-901c06f232c3',
        Type: 'Series',
        Name: 'Ted Lasso',
        OriginalTitle: 'Ted Lasso',
        ProductionYear: 2020,
        ProviderIds: { Imdb: 'tt10986410', Tmdb: '97546' },
    });

    await new Promise(resolve => setTimeout(resolve, 20));

    assert.deepEqual(JSON.parse(JSON.stringify(harness.getApiParams())), {
        itemId: '1eaff7b6-c537-d877-c83d-901c06f232c3',
    });
    assert.equal(harness.badges.length, 2);
    assert.equal(harness.getJsonCalls(), 1);
    assert.deepEqual(harness.badges.map(badge => badge.textContent), ['3,8/5', '4,4/5']);
    assert.deepEqual(
        harness.visualOrder.map(element => element.textContent ?? element.id),
        ['3,8/5', '4,4/5', 'native-rating']);
});

function deferred() {
    let resolve;
    let reject;
    const promise = new Promise((resolvePromise, rejectPromise) => {
        resolve = resolvePromise;
        reject = rejectPromise;
    });
    return { promise, reject, resolve };
}

function createNavigationHarness(items, getJSON) {
    const badges = [];
    const attributes = new Map();
    let getJsonCalls = 0;
    let currentId = Object.keys(items)[0];
    let observerCallback;
    let ratingGroup = null;

    const infoContainer = {
        appendChild(element) {
            element.remove = () => {
                element.children?.forEach(child => {
                    const index = badges.indexOf(child);
                    if (index >= 0) badges.splice(index, 1);
                });
                if (ratingGroup === element) ratingGroup = null;
            };
            ratingGroup = element;
            badges.push(...(element.children ?? []));
        },
        getAttribute: name => attributes.get(name) ?? null,
        querySelector: selector => selector === '.allocine-ratings-group' ? ratingGroup : null,
        removeAttribute: name => attributes.delete(name),
        setAttribute: (name, value) => attributes.set(name, value),
    };
    const apiClient = {
        getCurrentUserId: () => 'user-id',
        getItem: async (_userId, itemId) => items[itemId],
        getUrl: (_path, params) => params.itemId,
        getJSON: (...args) => {
            getJsonCalls += 1;
            return getJSON(...args);
        },
    };
    const location = { hash: `#/details?id=${currentId}` };
    const document = {
        body: {},
        head: { appendChild() {} },
        createElement: () => ({ style: {}, children: [], appendChild(child) { this.children.push(child); } }),
        getElementById: () => ({}),
        querySelector: selector => selector === '#itemDetailPage:not(.hide)'
            ? { querySelector: inner => inner === '.itemMiscInfo-primary' ? infoContainer : null }
            : null,
        querySelectorAll: selector => selector.includes('.allocine-ratings-group') && ratingGroup ? [ratingGroup] : [],
    };
    const context = {
        ApiClient: apiClient,
        MutationObserver: class {
            constructor(callback) { observerCallback = callback; }
            observe() {}
        },
        clearTimeout,
        console,
        document,
        isNaN,
        location,
        parseFloat,
        setTimeout,
        URLSearchParams,
        window: { ApiClient: apiClient, location },
    };
    vm.runInNewContext(source, context);

    return {
        attributes,
        badges,
        getJsonCalls: () => getJsonCalls,
        navigate(itemId) {
            currentId = itemId;
            location.hash = `#/details?id=${itemId}`;
            observerCallback([{ attributeName: 'class', target: { classList: { contains: () => false } } }]);
        },
        retrigger() {
            observerCallback([{ attributeName: 'class', target: { classList: { contains: () => false } } }]);
        },
        simulateExternalContainerRerender() {
            ratingGroup?.remove();
            observerCallback([{ type: 'childList', target: infoContainer }]);
        },
    };
}

const movieA = {
    Id: 'a', Type: 'Movie', Name: 'Movie A', OriginalTitle: 'Movie A',
    ProductionYear: 2024, ProviderIds: { Imdb: 'tt0000001', Tmdb: '1' },
};
const seriesB = {
    Id: 'b', Type: 'Series', Name: 'Series B', OriginalTitle: 'Series B',
    ProductionYear: 2025, ProviderIds: { Imdb: 'tt0000002', Tmdb: '2' },
};

test('late response from previous SPA route cannot replace current ratings', async () => {
    const first = deferred();
    const second = deferred();
    const harness = createNavigationHarness({ a: movieA, b: seriesB }, itemId =>
        itemId === 'a' ? first.promise : second.promise);

    await new Promise(resolve => setTimeout(resolve, 20));
    harness.navigate('b');
    await new Promise(resolve => setTimeout(resolve, 100));
    second.resolve({ presse: '4.5' });
    await new Promise(resolve => setTimeout(resolve, 0));
    first.resolve({ presse: '1.0' });
    await new Promise(resolve => setTimeout(resolve, 0));

    assert.deepEqual(harness.badges.map(badge => badge.textContent), ['4,5/5']);
    assert.equal(harness.attributes.get('data-allocine-processed'), 'b');
});

test('same-route mutations coalesce while a ratings request is pending', async () => {
    const pending = deferred();
    const harness = createNavigationHarness({ a: movieA }, () => pending.promise);

    await new Promise(resolve => setTimeout(resolve, 20));
    harness.retrigger();
    await new Promise(resolve => setTimeout(resolve, 100));

    assert.equal(harness.getJsonCalls(), 1);
    pending.resolve({ presse: '4.0' });
    await new Promise(resolve => setTimeout(resolve, 0));
});

test('external rerender that removes the rating group triggers restoration', async () => {
    const harness = createNavigationHarness({ a: movieA }, async () => ({ presse: '4.0' }));

    await new Promise(resolve => setTimeout(resolve, 20));
    assert.deepEqual(harness.badges.map(badge => badge.textContent), ['4,0/5']);
    harness.simulateExternalContainerRerender();
    await new Promise(resolve => setTimeout(resolve, 100));

    assert.equal(harness.getJsonCalls(), 2);
    assert.deepEqual(harness.badges.map(badge => badge.textContent), ['4,0/5']);
});

test('revisiting an item revalidates through the server cache instead of keeping browser data forever', async () => {
    const harness = createNavigationHarness({ a: movieA, b: seriesB }, async itemId => (
        itemId === 'a' ? { presse: '4.0' } : { public: '4.1' }));

    await new Promise(resolve => setTimeout(resolve, 20));
    harness.navigate('b');
    await new Promise(resolve => setTimeout(resolve, 100));
    harness.navigate('a');
    await new Promise(resolve => setTimeout(resolve, 100));

    assert.equal(harness.getJsonCalls(), 3);
});

test('rapid A to B to A navigation reuses the one in-flight request for A', async () => {
    const firstA = deferred();
    const firstB = deferred();
    const calls = [];
    const harness = createNavigationHarness({ a: movieA, b: seriesB }, itemId => {
        calls.push(itemId);
        return itemId === 'a' ? firstA.promise : firstB.promise;
    });

    await new Promise(resolve => setTimeout(resolve, 20));
    harness.navigate('b');
    await new Promise(resolve => setTimeout(resolve, 100));
    harness.navigate('a');
    await new Promise(resolve => setTimeout(resolve, 100));

    assert.deepEqual(calls, ['a', 'b']);
    firstA.resolve({ presse: '4.4' });
    firstB.resolve({ public: '2.0' });
    await new Promise(resolve => setTimeout(resolve, 0));
    assert.deepEqual(harness.badges.map(badge => badge.textContent), ['4,4/5']);
});

test('response does not render into a replaced detail-page container', async () => {
    const pending = deferred();
    let getJsonCalls = 0;
    const createTarget = () => {
        const attributes = new Map();
        let ratingGroup = null;
        return {
            attributes,
            badges: [],
            isConnected: true,
            appendChild(element) {
                ratingGroup = element;
                this.badges.push(...(element.children ?? []));
            },
            getAttribute: name => attributes.get(name) ?? null,
            querySelector: selector => selector === '.allocine-ratings-group' ? ratingGroup : null,
            removeAttribute: name => attributes.delete(name),
            setAttribute: (name, value) => attributes.set(name, value),
        };
    };
    const originalTarget = createTarget();
    let currentTarget = originalTarget;
    const apiClient = {
        getCurrentUserId: () => 'user-id',
        getItem: async () => movieA,
        getUrl: () => 'Movie A',
        getJSON: () => {
            getJsonCalls += 1;
            return getJsonCalls === 1 ? pending.promise : Promise.resolve({ presse: '4.3' });
        },
    };
    const location = { hash: '#/details?id=a' };
    const document = {
        body: {},
        head: { appendChild() {} },
        createElement: () => ({ style: {}, children: [], appendChild(child) { this.children.push(child); } }),
        getElementById: () => ({}),
        querySelector: selector => selector === '#itemDetailPage:not(.hide)'
            ? { querySelector: inner => inner === '.itemMiscInfo-primary' ? currentTarget : null }
            : null,
        querySelectorAll: () => [],
    };
    const context = {
        ApiClient: apiClient,
        MutationObserver: class { observe() {} },
        clearTimeout,
        console,
        document,
        isNaN,
        location,
        parseFloat,
        setTimeout,
        URLSearchParams,
        window: { ApiClient: apiClient, location },
    };
    vm.runInNewContext(source, context);

    await new Promise(resolve => setTimeout(resolve, 20));
    originalTarget.isConnected = false;
    currentTarget = createTarget();
    pending.resolve({ presse: '4.2' });
    await new Promise(resolve => setTimeout(resolve, 100));

    assert.equal(originalTarget.badges.length, 0);
    assert.equal(originalTarget.attributes.has('data-allocine-processed'), false);
    assert.equal(getJsonCalls, 2);
    assert.deepEqual(currentTarget.badges.map(badge => badge.textContent), ['4,3/5']);
});

function createReplacementFailureHarness(getItem, getJSON) {
    const createTarget = () => {
        const attributes = new Map();
        let ratingGroup = null;
        return {
            attributes,
            badges: [],
            isConnected: true,
            appendChild(element) {
                ratingGroup = element;
                this.badges.push(...(element.children ?? []));
            },
            getAttribute: name => attributes.get(name) ?? null,
            querySelector: selector => selector === '.allocine-ratings-group' ? ratingGroup : null,
            removeAttribute: name => attributes.delete(name),
            setAttribute: (name, value) => attributes.set(name, value),
        };
    };
    const originalTarget = createTarget();
    let currentTarget = originalTarget;
    const apiClient = {
        getCurrentUserId: () => 'user-id',
        getItem,
        getUrl: () => 'Movie A',
        getJSON,
    };
    const location = { hash: '#/details?id=a' };
    const document = {
        body: {},
        head: { appendChild() {} },
        createElement: () => ({ style: {}, children: [], appendChild(child) { this.children.push(child); } }),
        getElementById: () => ({}),
        querySelector: selector => selector === '#itemDetailPage:not(.hide)'
            ? { querySelector: inner => inner === '.itemMiscInfo-primary' ? currentTarget : null }
            : null,
        querySelectorAll: () => [],
    };
    const context = {
        ApiClient: apiClient,
        MutationObserver: class { observe() {} },
        clearTimeout,
        console,
        document,
        isNaN,
        location,
        parseFloat,
        setTimeout,
        URLSearchParams,
        window: { ApiClient: apiClient, location },
    };
    vm.runInNewContext(source, context);
    return {
        currentTarget: () => currentTarget,
        originalTarget,
        replaceTarget() {
            originalTarget.isConnected = false;
            currentTarget = createTarget();
        },
    };
}

test('ratings rejection retries against a same-route replacement container', async () => {
    const pending = deferred();
    let ratingsCalls = 0;
    const harness = createReplacementFailureHarness(
        async () => movieA,
        () => {
            ratingsCalls += 1;
            return ratingsCalls === 1 ? pending.promise : Promise.resolve({ presse: '4.6' });
        });

    await new Promise(resolve => setTimeout(resolve, 20));
    harness.replaceTarget();
    pending.reject(new Error('temporary ratings failure'));
    await new Promise(resolve => setTimeout(resolve, 100));

    assert.equal(ratingsCalls, 2);
    assert.deepEqual(harness.currentTarget().badges.map(badge => badge.textContent), ['4,6/5']);
});

test('failed request is mutation-cooled down and retries after route revisit', async () => {
    let seriesAttempts = 0;
    const harness = createNavigationHarness({ a: movieA, b: seriesB }, async itemId => {
        if (itemId === 'a') return { presse: '3.0' };
        seriesAttempts += 1;
        if (seriesAttempts === 1) throw new Error('temporary failure');
        return { public: '4.8' };
    });

    await new Promise(resolve => setTimeout(resolve, 20));
    assert.equal(harness.badges.length, 1);
    harness.navigate('b');
    await new Promise(resolve => setTimeout(resolve, 100));
    assert.equal(harness.badges.length, 0);
    assert.equal(harness.attributes.has('data-allocine-processed'), false);

    for (let index = 0; index < 5; index += 1) harness.retrigger();
    await new Promise(resolve => setTimeout(resolve, 100));
    assert.equal(seriesAttempts, 1);

    harness.navigate('a');
    await new Promise(resolve => setTimeout(resolve, 100));
    harness.navigate('b');
    await new Promise(resolve => setTimeout(resolve, 100));
    assert.deepEqual(harness.badges.map(badge => badge.textContent), ['4,8/5']);
    assert.equal(seriesAttempts, 2);
});

test('synchronous ApiClient failure remains retryable after leaving and revisiting the route', async () => {
    let attempts = 0;
    const harness = createNavigationHarness({ a: movieA, b: seriesB }, itemId => {
        if (itemId === 'b') return Promise.resolve({ public: '3.1' });
        attempts += 1;
        if (attempts === 1) throw new Error('synchronous failure');
        return Promise.resolve({ presse: '4.2' });
    });

    await new Promise(resolve => setTimeout(resolve, 20));
    harness.retrigger();
    await new Promise(resolve => setTimeout(resolve, 100));
    assert.equal(attempts, 1);

    harness.navigate('b');
    await new Promise(resolve => setTimeout(resolve, 100));
    harness.navigate('a');
    await new Promise(resolve => setTimeout(resolve, 100));

    assert.equal(harness.getJsonCalls(), 3);
    assert.equal(attempts, 2);
    assert.deepEqual(harness.badges.map(badge => badge.textContent), ['4,2/5']);
});

test('ratings request does not wait for a redundant Jellyfin metadata lookup', async () => {
    const pendingMetadata = deferred();
    let ratingsCalls = 0;
    const target = {
        appendChild() {}, getAttribute: () => null, querySelector: () => null,
        removeAttribute() {}, setAttribute() {},
    };
    const apiClient = {
        getCurrentUserId: () => 'user-id',
        getItem: () => pendingMetadata.promise,
        getUrl: (_path, params) => params.itemId,
        getJSON: async () => {
            ratingsCalls += 1;
            return { public: '4.1' };
        },
    };
    const location = { hash: '#/details?id=fast-item' };
    const context = {
        ApiClient: apiClient,
        MutationObserver: class { observe() {} },
        clearTimeout, console, isNaN, location, parseFloat, setTimeout, URLSearchParams,
        document: {
            body: {}, head: { appendChild() {} }, createElement: () => ({ style: {}, children: [], appendChild(child) { this.children.push(child); } }),
            getElementById: () => ({}),
            querySelector: selector => selector === '#itemDetailPage:not(.hide)'
                ? { querySelector: () => target }
                : null,
            querySelectorAll: () => [],
        },
        window: { ApiClient: apiClient, location },
    };

    vm.runInNewContext(source, context);
    await new Promise(resolve => setTimeout(resolve, 20));

    assert.equal(ratingsCalls, 1);
});

test('without native ratings the AlloCiné group stays immediately after parental classification', async () => {
    const officialRating = { id: 'parental-classification', nextSibling: null };
    const enhancedMetadata = { id: 'enhanced-metadata', nextSibling: null };
    const visualOrder = [officialRating, enhancedMetadata];
    const attributes = new Map();
    let ratingGroup = null;
    const syncSiblings = () => visualOrder.forEach((element, index) => {
        element.nextSibling = visualOrder[index + 1] ?? null;
        element.parentNode = target;
    });
    const target = {
        isConnected: true,
        appendChild(element) {
            visualOrder.push(element);
            ratingGroup = element;
            syncSiblings();
        },
        getAttribute: name => attributes.get(name) ?? null,
        insertBefore(element, reference) {
            const previous = visualOrder.indexOf(element);
            if (previous >= 0) visualOrder.splice(previous, 1);
            visualOrder.splice(visualOrder.indexOf(reference), 0, element);
            ratingGroup = element;
            syncSiblings();
        },
        querySelector(selector) {
            if (selector === '.allocine-ratings-group') return ratingGroup;
            if (selector === '.mediaInfoOfficialRating') return officialRating;
            return null;
        },
        removeAttribute: name => attributes.delete(name),
        setAttribute: (name, value) => attributes.set(name, value),
    };
    syncSiblings();
    const apiClient = {
        getUrl: (_path, params) => params.itemId,
        getJSON: async () => ({ public: '4.1' }),
    };
    const location = { hash: '#/details?id=position-item' };
    const context = {
        ApiClient: apiClient,
        MutationObserver: class { observe() {} },
        clearTimeout, console, isNaN, location, parseFloat, setTimeout, URLSearchParams,
        document: {
            body: {}, head: { appendChild() {} },
            createElement: () => ({ style: {}, children: [], appendChild(child) { this.children.push(child); } }),
            getElementById: () => ({}),
            querySelector: selector => selector === '#itemDetailPage:not(.hide)'
                ? { querySelector: () => target }
                : null,
            querySelectorAll: () => [],
        },
        window: { ApiClient: apiClient, location },
    };

    vm.runInNewContext(source, context);
    await new Promise(resolve => setTimeout(resolve, 20));

    assert.deepEqual(
        visualOrder.map(element => element.id ?? element.className),
        ['parental-classification', 'allocine-ratings-group', 'enhanced-metadata']);
});

test('Jellyfin star rating is treated as the first native rating even without parental classification', async () => {
    const jellyfinStarRating = { id: 'jellyfin-star-rating', nextSibling: null };
    const visualOrder = [jellyfinStarRating];
    const attributes = new Map();
    let ratingGroup = null;
    const syncSiblings = () => visualOrder.forEach((element, index) => {
        element.nextSibling = visualOrder[index + 1] ?? null;
        element.parentNode = target;
    });
    const target = {
        isConnected: true,
        appendChild(element) {
            visualOrder.push(element);
            ratingGroup = element;
            syncSiblings();
        },
        getAttribute: name => attributes.get(name) ?? null,
        insertBefore(element, reference) {
            const previous = visualOrder.indexOf(element);
            if (previous >= 0) visualOrder.splice(previous, 1);
            visualOrder.splice(visualOrder.indexOf(reference), 0, element);
            ratingGroup = element;
            syncSiblings();
        },
        querySelector(selector) {
            if (selector === '.allocine-ratings-group') return ratingGroup;
            if (selector.includes('.starRatingContainer')) return jellyfinStarRating;
            return null;
        },
        removeAttribute: name => attributes.delete(name),
        setAttribute: (name, value) => attributes.set(name, value),
    };
    syncSiblings();
    const apiClient = {
        getUrl: (_path, params) => params.itemId,
        getJSON: async () => ({ public: '4.1' }),
    };
    const location = { hash: '#/details?id=star-rating-item' };
    const context = {
        ApiClient: apiClient,
        MutationObserver: class { observe() {} },
        clearTimeout, console, isNaN, location, parseFloat, setTimeout, URLSearchParams,
        document: {
            body: {}, head: { appendChild() {} },
            createElement: () => ({ style: {}, children: [], appendChild(child) { this.children.push(child); } }),
            getElementById: () => ({}),
            querySelector: selector => selector === '#itemDetailPage:not(.hide)'
                ? { querySelector: () => target }
                : null,
            querySelectorAll: () => [],
        },
        window: { ApiClient: apiClient, location },
    };

    vm.runInNewContext(source, context);
    await new Promise(resolve => setTimeout(resolve, 20));

    assert.deepEqual(
        visualOrder.map(element => element.id ?? element.className),
        ['allocine-ratings-group', 'jellyfin-star-rating']);
});

test('nested rating anchor is skipped in favor of a later direct native rating', async () => {
    const wrapper = { id: 'enhanced-wrapper' };
    const nestedRating = { id: 'nested-rating', parentNode: wrapper };
    const directNativeRating = { id: 'direct-native-rating' };
    const visualOrder = [wrapper, directNativeRating];
    const attributes = new Map();
    let ratingGroup = null;
    let requests = 0;
    const target = {
        isConnected: true,
        appendChild(element) {
            element.parentNode = this;
            visualOrder.push(element);
            ratingGroup = element;
        },
        getAttribute: name => attributes.get(name) ?? null,
        insertBefore(element, reference) {
            assert.equal(reference, directNativeRating);
            const previous = visualOrder.indexOf(element);
            if (previous >= 0) visualOrder.splice(previous, 1);
            visualOrder.splice(visualOrder.indexOf(reference), 0, element);
        },
        querySelector(selector) {
            if (selector === '.allocine-ratings-group') return ratingGroup;
            if (selector.includes('.starRatingContainer')) return nestedRating;
            return null;
        },
        querySelectorAll(selector) {
            return selector.includes('.starRatingContainer')
                ? [nestedRating, directNativeRating]
                : [];
        },
        removeAttribute: name => attributes.delete(name),
        setAttribute: (name, value) => attributes.set(name, value),
    };
    directNativeRating.parentNode = target;
    const apiClient = {
        getUrl: (_path, params) => params.itemId,
        getJSON: async () => {
            requests += 1;
            return { public: '4.0' };
        },
    };
    const location = { hash: '#/details?id=nested-anchor-item' };
    const context = {
        ApiClient: apiClient,
        MutationObserver: class { observe() {} },
        clearTimeout, console, Date, isNaN, location, parseFloat, setTimeout, URLSearchParams,
        document: {
            body: {}, head: { appendChild() {} },
            createElement: () => ({ style: {}, children: [], appendChild(child) { this.children.push(child); } }),
            getElementById: () => ({}),
            querySelector: selector => selector === '#itemDetailPage:not(.hide)'
                ? { querySelector: () => target }
                : null,
            querySelectorAll: () => [],
        },
        window: { ApiClient: apiClient, location },
    };

    vm.runInNewContext(source, context);
    await new Promise(resolve => setTimeout(resolve, 20));

    assert.equal(requests, 1);
    assert.deepEqual(visualOrder.map(element => element.id ?? element.className), [
        'enhanced-wrapper',
        'allocine-ratings-group',
        'direct-native-rating',
    ]);
});

test('late native ratings reposition the AlloCiné group immediately before them', async () => {
    const visualOrder = [];
    let observerCallback;
    let nativeRating = null;
    let ratingGroup = null;
    const attributes = new Map();
    const syncSiblings = () => visualOrder.forEach((element, index) => {
        element.nextSibling = visualOrder[index + 1] ?? null;
    });
    const target = {
        isConnected: true,
        appendChild(element) {
            const previous = visualOrder.indexOf(element);
            if (previous >= 0) visualOrder.splice(previous, 1);
            visualOrder.push(element);
            element.parentNode = this;
            ratingGroup = element;
            syncSiblings();
        },
        getAttribute: name => attributes.get(name) ?? null,
        insertBefore(element, reference) {
            const previous = visualOrder.indexOf(element);
            if (previous >= 0) visualOrder.splice(previous, 1);
            visualOrder.splice(visualOrder.indexOf(reference), 0, element);
            element.parentNode = this;
            ratingGroup = element;
            syncSiblings();
        },
        querySelector: selector => selector === '.allocine-ratings-group'
            ? ratingGroup
            : selector.includes('mediaInfoCriticRating') ? nativeRating : null,
        removeAttribute: name => attributes.delete(name),
        setAttribute: (name, value) => attributes.set(name, value),
    };
    const apiClient = {
        getUrl: (_path, params) => params.itemId,
        getJSON: async () => ({ presse: '3.9', public: '4.2' }),
    };
    const location = { hash: '#/details?id=placement-item' };
    const document = {
        body: {}, head: { appendChild() {} },
        createElement: () => ({ style: {}, appendChild(child) { (this.children ??= []).push(child); } }),
        getElementById: () => ({}),
        querySelector: selector => selector === '#itemDetailPage:not(.hide)'
            ? { querySelector: () => target }
            : null,
        querySelectorAll: () => [],
    };
    const context = {
        ApiClient: apiClient,
        MutationObserver: class {
            constructor(callback) { observerCallback = callback; }
            observe() {}
        },
        clearTimeout, console, document, isNaN, location, parseFloat, setTimeout, URLSearchParams,
        window: { ApiClient: apiClient, location },
    };

    vm.runInNewContext(source, context);
    await new Promise(resolve => setTimeout(resolve, 20));
    nativeRating = { id: 'native-rating', parentNode: target };
    visualOrder.unshift(nativeRating);
    syncSiblings();
    observerCallback([{ attributeName: 'class', target: { classList: { contains: () => false } } }]);
    await new Promise(resolve => setTimeout(resolve, 100));

    assert.equal(visualOrder.at(-1), nativeRating);
    assert.equal(visualOrder.at(-2).className, 'allocine-ratings-group');
});
