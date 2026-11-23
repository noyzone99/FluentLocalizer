// Run with: node Tests/FluentLocalizer.WebApp.Tests/site.test.cjs
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");
const source = fs.readFileSync(path.join(__dirname, "../../Sources/FluentLocalizer.WebApp/wwwroot/js/site.js"), "utf8");
const defaultMatchMedia = () => ({ matches: false, addEventListener() {} });

function site(saved, blocked = false, navigator = { languages: ["it-CH", "en-US"], language: "it-CH" }, matchMedia = defaultMatchMedia) {
    const values = new Map([["fluentlocalizer.language", saved]]);
    const context = {
        navigator,
        window: {
            matchMedia,
            localStorage: {
                getItem(key) { if (blocked) throw new Error("Storage denied"); return values.get(key); },
                setItem(key, value) { if (blocked) throw new Error("Storage denied"); values.set(key, value); }
            }
        },
        document: { documentElement: { dataset: {} }, addEventListener() {} }
    };
    vm.runInNewContext(source, context);
    return context.window.fluentSite;
}

assert.deepEqual(Array.from(site().getLanguages()), ["it-CH", "en-US"]);
for (const language of ["en", "it", "fr", "de", "es"]) {
    const app = site();
    app.saveLanguage(language);
    assert.deepEqual(Array.from(app.getLanguages()), [language, "it-CH", "en-US"]);
    assert.deepEqual(Array.from(site(language).getLanguages()), [language, "it-CH", "en-US"]);
    app.saveLanguage("ja");
    assert.equal(app.getLanguages()[0], language);
}
assert.deepEqual(Array.from(site("invalid").getLanguages()), ["it-CH", "en-US"]);
const blocked = site("fr", true);
assert.doesNotThrow(() => blocked.saveLanguage("de"));
assert.deepEqual(Array.from(blocked.getLanguages()), ["it-CH", "en-US"]);
assert.deepEqual(Array.from(site(null, false, { language: "es-MX" }).getLanguages()), ["es-MX"]);

const menuListeners = new Set();
const narrowScreen = {
    matches: true,
    addEventListener(type, callback) {
        assert.equal(type, "change");
        menuListeners.add(callback);
    },
    removeEventListener(type, callback) {
        assert.equal(type, "change");
        menuListeners.delete(callback);
    }
};
const docsSite = site(null, false, undefined, query => query === "(max-width: 760px)" ? narrowScreen : defaultMatchMedia());
const firstMenu = {};
const secondMenu = {};
const firstWatcher = docsSite.collapseOnNarrow(firstMenu);
const secondWatcher = docsSite.collapseOnNarrow(secondMenu);
assert.equal(firstMenu.open, false);
assert.equal(secondMenu.open, false);
assert.equal(menuListeners.size, 2);

firstWatcher.dispose();
assert.equal(menuListeners.size, 1);
for (const listener of menuListeners) listener({ matches: false });
assert.equal(firstMenu.open, false);
assert.equal(secondMenu.open, true);

secondWatcher.dispose();
assert.equal(menuListeners.size, 0);
console.log("Site language preferences and documentation menu cleanup passed.");
