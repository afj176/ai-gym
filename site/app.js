// Optional: show the latest release's version and asset sizes. The static text stays if this fails.
(function () {
  if (!window.fetch) return;
  var assets = { setup: "AiGym-Setup-x64.exe", zip: "AiGym-win-x64.zip" };
  fetch("https://api.github.com/repos/afj176/ai-gym/releases/latest", { headers: { Accept: "application/vnd.github+json" } })
    .then(function (r) { return r.ok ? r.json() : null; })
    .then(function (release) {
      if (!release || !release.tag_name) return;
      var version = String(release.tag_name).replace(/^v/i, "");
      if (!/^\d+\.\d+(\.\d+)?$/.test(version)) return;
      document.querySelectorAll("[data-version]").forEach(function (el) { el.textContent = version; });
      (release.assets || []).forEach(function (asset) {
        Object.keys(assets).forEach(function (key) {
          if (asset.name !== assets[key] || !(asset.size > 0)) return;
          var mb = Math.round(asset.size / 1e6) + " MB";
          document.querySelectorAll('[data-size="' + key + '"]').forEach(function (el) { el.textContent = mb; });
        });
      });
    })
    .catch(function () { /* keep the static text */ });
})();
