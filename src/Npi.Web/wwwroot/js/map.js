// Results map (CLAUDE.md §7 Stage 5.5 item 10): one pin per ZIP code for the providers on this page, placed at the
// ZIP's Census centroid. The section stays hidden without JavaScript or when Leaflet didn't load; the table is the
// accessible view of the same data.
(() => {
  const section = document.getElementById("map-section");
  const element = document.getElementById("results-map");
  const data = document.getElementById("map-data");
  if (!section || !element || !data || !window.L) return;

  let pins;
  try {
    pins = JSON.parse(data.textContent);
  } catch {
    return;
  }
  if (!Array.isArray(pins) || pins.length === 0) return;

  section.hidden = false;
  const map = L.map(element, { scrollWheelZoom: false });
  L.tileLayer(element.dataset.tileUrl, {
    maxZoom: Number(element.dataset.maxZoom) || 19,
    attribution: element.dataset.attribution,
  }).addTo(map);

  const plural = (n, word) => `${n} ${word}${n === 1 ? "" : "s"}`;

  // Popup content is built from DOM nodes with textContent, never HTML strings: names come from the data.
  const popup = (pin) => {
    const root = document.createElement("div");
    const title = document.createElement("strong");
    title.textContent = `ZIP ${pin.zip} · ${plural(pin.providers.length, "provider")}`;
    const list = document.createElement("ul");
    for (const provider of pin.providers) {
      const item = document.createElement("li");
      const link = document.createElement("a");
      link.href = "/provider/" + encodeURIComponent(provider.npi);
      link.textContent = provider.name;
      item.append(link);
      const details = [provider.specialty, provider.address].filter(Boolean).join(" · ");
      if (details) {
        const line = document.createElement("span");
        line.className = "muted";
        line.textContent = details;
        item.append(document.createElement("br"), line);
      }
      list.append(item);
    }
    root.append(title, list);
    return root;
  };

  const bounds = [];
  for (const pin of pins) {
    const size = Math.min(6 + 2 * (pin.providers.length - 1), 16);
    L.circleMarker([pin.lat, pin.lon], { radius: size, weight: 2, color: "#0b5cad", fillColor: "#0b5cad", fillOpacity: 0.45 })
      .bindPopup(() => popup(pin), { autoPanPaddingTopLeft: L.point(48, 12) }) // clear of the zoom buttons
      .bindTooltip(`ZIP ${pin.zip} · ${plural(pin.providers.length, "provider")}`)
      .addTo(map);
    bounds.push([pin.lat, pin.lon]);
  }
  // Fit all pins, and fit again whenever the map's size changes (a page loaded in a background tab or a resized
  // window starts with the wrong size), until the visitor moves or zooms the map themselves.
  const fit = () => map.fitBounds(bounds, { padding: [24, 24], maxZoom: 12 });
  let touched = false;
  for (const type of ["pointerdown", "wheel", "keydown"]) element.addEventListener(type, () => { touched = true; }, { passive: true });
  fit();
  new ResizeObserver(() => {
    map.invalidateSize();
    if (!touched) fit();
  }).observe(element);
})();
