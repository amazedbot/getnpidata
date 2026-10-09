// Map search page (CLAUDE.md §7 Stage 5.5 item 10). "Search in this area" asks /map/search for the providers inside
// the visible map area that match the filters; each street address becomes one pin (hover: who practices there;
// click: the same list with links), and the table lists every provider at the pins currently in view.
(() => {
  const element = document.getElementById("area-map");
  if (!element || !window.L) return;

  const form = document.getElementById("search-form");
  const searchButton = document.getElementById("search-area");
  const nearMe = document.getElementById("near-me");
  const status = document.getElementById("map-status");
  const count = document.getElementById("area-count");
  const tbody = document.querySelector("#area-table tbody");
  const sortBy = document.getElementById("area-sort");
  const sortOrder = document.getElementById("area-order");
  const maxLatSpan = Number(element.dataset.maxLatSpan);
  const maxLonSpan = Number(element.dataset.maxLonSpan);

  const map = L.map(element);
  L.tileLayer(element.dataset.tileUrl, {
    maxZoom: Number(element.dataset.maxZoom) || 19,
    attribution: element.dataset.attribution,
  }).addTo(map);
  const layer = L.layerGroup().addTo(map);

  const parts = element.dataset.start ? element.dataset.start.split(",").map(Number) : [];
  const start = parts.length === 4 && parts.every(Number.isFinite) ? L.latLngBounds([parts[1], parts[0]], [parts[3], parts[2]]) : null;
  if (start) {
    map.fitBounds(start);
  } else {
    map.setView([39.5, -98.35], 4); // the contiguous US
  }

  // A page loaded in a background tab starts with the wrong size: keep Leaflet's idea of it current and, until the
  // visitor moves the map, keep showing the start area.
  let touched = false;
  for (const type of ["pointerdown", "wheel", "keydown"]) element.addEventListener(type, () => { touched = true; }, { passive: true });
  new ResizeObserver(() => {
    map.invalidateSize();
    if (start && !touched) map.fitBounds(start);
  }).observe(element);

  let pins = [];
  let searching = false;

  const plural = (n, one, many = one + "s") => `${n.toLocaleString()} ${n === 1 ? one : many}`;
  const tooLarge = (b) => b.getNorth() - b.getSouth() > maxLatSpan || b.getEast() - b.getWest() > maxLonSpan;
  const el = (tag, text, className) => {
    const node = document.createElement(tag);
    if (text != null) node.textContent = text;
    if (className) node.className = className;
    return node;
  };
  const addressLine = (p) => [p.address1, p.city, [p.state, p.zip].filter(Boolean).join(" ")].filter(Boolean).join(", ");

  const updateButton = () => {
    const big = tooLarge(map.getBounds());
    searchButton.disabled = searching || big;
    searchButton.textContent = big ? "Zoom in to search" : "Search in this area";
  };

  const filterParams = () => {
    const params = new URLSearchParams();
    for (const field of form.elements) {
      if (field.name && field.value && !field.disabled) params.set(field.name, field.value);
    }
    return params;
  };

  // Searches the visible area, or the given bounds (the start area, before the map has its real size).
  async function search(area) {
    const bounds = area instanceof L.LatLngBounds ? area : map.getBounds();
    if (tooLarge(bounds)) {
      status.textContent = "Zoom in to search: the map shows too large an area.";
      return;
    }

    const params = filterParams();
    params.set("bbox", bounds.toBBoxString());
    searching = true;
    updateButton();
    status.textContent = "Searching…";
    try {
      const response = await fetch("/map/search?" + params.toString());
      const body = await response.json();
      if (!response.ok) {
        status.textContent = Object.values(body.errors || {}).flat().join(" ") || "The search failed.";
        return;
      }

      history.replaceState(null, "", "/map?" + params.toString());
      draw(body);
    } catch {
      status.textContent = "The search failed. Try again.";
    } finally {
      searching = false;
      updateButton();
    }
  }

  // One pin per street address: providers whose points coincide share it.
  function draw(result) {
    layer.clearLayers();
    const byPoint = new Map();
    for (const item of result.items) {
      const key = `${item.lat.toFixed(5)},${item.lon.toFixed(5)}`;
      if (!byPoint.has(key)) byPoint.set(key, { lat: item.lat, lon: item.lon, approximate: item.approximate, items: [] });
      byPoint.get(key).items.push(item);
    }

    pins = [...byPoint.values()];
    for (const pin of pins) {
      const size = Math.min(6 + 1.5 * (pin.items.length - 1), 14);
      pin.style = pin.approximate
        ? { radius: size, weight: 2, color: "#6e7781", dashArray: "3 3", fillColor: "#afb8c1", fillOpacity: 0.5 }
        : { radius: size, weight: 2, color: "#0b5cad", fillColor: "#0b5cad", fillOpacity: 0.55 };
      pin.marker = L.circleMarker([pin.lat, pin.lon], pin.style)
        .bindTooltip(() => pinContent(pin, false), { direction: "top", offset: [0, -size], className: "pin-tooltip" })
        .bindPopup(() => pinContent(pin, true), { maxWidth: 320, autoPanPaddingTopLeft: L.point(48, 12) })
        .on("tooltipopen", (e) => {
          // Open towards the middle of the map, so a pin near the top edge doesn't cut its list off.
          const below = map.latLngToContainerPoint([pin.lat, pin.lon]).y < map.getSize().y / 2;
          e.tooltip.options.direction = below ? "bottom" : "top";
          e.tooltip.options.offset = [0, below ? size : -size];
          e.tooltip.update();
        })
        .on("mouseover", () => highlightRows(pin, true))
        .on("mouseout", () => highlightRows(pin, false))
        .addTo(layer);
    }

    const total = result.items.length;
    status.textContent = total === 0
      ? "No providers match in this area."
      : `${plural(total, "provider")} at ${plural(pins.length, "address", "addresses")}` + (result.truncated
        ? ` (the ${result.limit.toLocaleString()} closest to the center; zoom in or add filters to see all)`
        : "");
    renderTable();
  }

  // Hover (tooltip): names only, at most 10. Click (popup): everyone, with links to their detail pages.
  function pinContent(pin, withLinks) {
    const root = el("div", null, "pin-content");
    // Usually everyone here shares one address; a ZIP-center pin (or a building with several spellings) can mix them.
    const shared = new Set(pin.items.map((i) => addressLine(i.provider))).size === 1;
    const zip = (pin.items[0].provider.zip || "").slice(0, 5);
    root.append(el("strong", shared ? addressLine(pin.items[0].provider) : pin.approximate ? `ZIP ${zip}` : "Several addresses at this point"));
    if (pin.approximate) root.append(el("div", "Approximate location (ZIP code center)", "muted"));
    const list = el("ul");
    const shown = withLinks ? pin.items : pin.items.slice(0, 10);
    for (const item of shown) {
      const p = item.provider;
      const li = el("li");
      const name = [p.name, p.credential].filter(Boolean).join(", ");
      if (withLinks) {
        const link = el("a", name);
        link.href = "/provider/" + encodeURIComponent(p.npi);
        li.append(link);
      } else {
        li.append(el("span", name));
      }
      if (p.primarySpecialty) li.append(el("span", " — " + p.primarySpecialty, "muted"));
      if (!shared) li.append(el("br"), el("span", addressLine(p), "muted"));
      list.append(li);
    }
    root.append(list);
    if (shown.length < pin.items.length) root.append(el("div", `+ ${pin.items.length - shown.length} more — click the pin for all`, "muted"));
    return root;
  }

  const compare = {
    distance: (a, b) => a.distance - b.distance,
    name: (a, b) => a.item.provider.name.localeCompare(b.item.provider.name),
    specialty: (a, b) => (a.item.provider.primarySpecialty || "").localeCompare(b.item.provider.primarySpecialty || ""),
    city: (a, b) => (a.item.provider.city || "").localeCompare(b.item.provider.city || ""),
    updated: (a, b) => (a.item.provider.lastUpdateDate || "").localeCompare(b.item.provider.lastUpdateDate || ""),
  };

  // The table: every provider at the pins inside the current view.
  function renderTable() {
    const view = map.getBounds();
    const center = map.getCenter();
    const visible = pins.filter((pin) => view.contains([pin.lat, pin.lon]));
    const rows = visible.flatMap((pin) => pin.items.map((item) => ({
      pin, item, distance: center.distanceTo([pin.lat, pin.lon]) / 1609.344,
    })));
    const order = sortOrder.value === "desc" ? -1 : 1;
    const byKey = compare[sortBy.value] || compare.distance;
    rows.sort((a, b) => order * byKey(a, b) || compare.distance(a, b));

    tbody.replaceChildren();
    for (const row of rows) {
      const p = row.item.provider;
      const tr = el("tr");
      const who = el("td");
      const link = el("a", p.name);
      link.href = "/provider/" + encodeURIComponent(p.npi);
      who.append(link);
      if (p.credential) who.append(el("span", ", " + p.credential));
      if (p.primarySpecialty) who.append(el("br"), el("span", p.primarySpecialty, "muted small"));
      const where = el("td", addressLine(p));
      if (row.pin.approximate) where.append(el("span", " (approximate)", "muted small"));
      tr.append(who, where, el("td", formatPhone(p.phone)), el("td", row.distance.toFixed(1), "num"));
      tr.addEventListener("mouseenter", () => highlightPin(row.pin, true));
      tr.addEventListener("mouseleave", () => highlightPin(row.pin, false));
      tbody.append(tr);
      row.tr = tr;
    }

    for (const pin of pins) pin.rows = rows.filter((r) => r.pin === pin).map((r) => r.tr);
    count.textContent = pins.length === 0
      ? "Search an area to list its providers."
      : `${plural(rows.length, "provider")} at ${plural(visible.length, "address", "addresses")} in view.`;
  }

  function highlightPin(pin, on) {
    pin.marker.setStyle(on ? { weight: 4, color: "#d4a72c" } : pin.style);
    if (on) pin.marker.bringToFront().openTooltip(); else pin.marker.closeTooltip();
  }

  function highlightRows(pin, on) {
    for (const tr of pin.rows || []) tr.classList.toggle("highlight", on);
  }

  function formatPhone(phone) {
    return phone && /^\d{10}$/.test(phone) ? `(${phone.slice(0, 3)}) ${phone.slice(3, 6)}-${phone.slice(6)}` : phone || "";
  }

  map.on("moveend", () => {
    updateButton();
    renderTable();
  });
  sortBy.addEventListener("change", renderTable);
  sortOrder.addEventListener("change", renderTable);
  searchButton.addEventListener("click", () => search());
  form.addEventListener("submit", (e) => {
    e.preventDefault();
    search();
  }, { capture: true });

  // "Near me": centre the map on the browser's location (it stays in the browser) and search around it.
  // Browsers offer geolocation only on secure pages (https or localhost).
  if ("geolocation" in navigator && window.isSecureContext) {
    nearMe.hidden = false;
    nearMe.addEventListener("click", () => {
      status.textContent = "Finding your location…";
      navigator.geolocation.getCurrentPosition((position) => {
        map.setView([position.coords.latitude, position.coords.longitude], 13);
        search();
      }, (error) => {
        status.textContent = error.code === error.PERMISSION_DENIED
          ? "Location access was blocked. Allow it in your browser, or move the map yourself."
          : "Your location isn't available right now.";
      }, { enableHighAccuracy: false, timeout: 15000, maximumAge: 600000 });
    });
  }

  updateButton();
  if (element.dataset.autoSearch === "true") {
    map.whenReady(() => search(start));
  } else if (!start) {
    status.textContent = "Zoom in on an area, then choose Search in this area.";
  }
})();
