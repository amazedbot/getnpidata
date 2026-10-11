// Dependent dropdowns: Classification → Specialization, State → County, State/County → City.
// Works without JavaScript too: the server fills the dependent list after a search.
// Used by the search page and the map page (which has no location fields).
(() => {
  for (const parent of document.querySelectorAll("select[data-dependent]")) {
    const child = document.getElementById(parent.dataset.dependent);
    parent.addEventListener("change", async () => {
      child.length = 1; // keep "Any"
      child.disabled = true;
      if (!parent.value) return;
      try {
        const response = await fetch(parent.dataset.source + encodeURIComponent(parent.value));
        if (!response.ok) return;
        for (const item of await response.json()) {
          const option = document.createElement("option");
          if (typeof item === "string") {
            option.value = item;
            option.textContent = item;
          } else {
            option.value = item.fips;
            option.textContent = item.name;
          }
          child.append(option);
        }
        child.disabled = child.length <= 1;
      } catch {
        // Leave the dependent list empty; the search still works without it.
      }
    });
  }

  // City: the cities of the chosen state, or of the chosen county.
  const city = document.getElementById("city");
  const state = document.getElementById("state");
  const county = document.getElementById("county");
  if (city && state) {
    let request = 0;
    const loadCities = async () => {
      const mine = ++request;
      city.length = 1;
      city.disabled = true;
      city.options[0].textContent = state.value ? "Any" : "Choose a state first";
      if (!state.value) return;
      // The county list reloads too when the state changes; only use it once it belongs to this state.
      const fips = county && county.value && county.value.length === 5 ? county.value : "";
      const query = "?state=" + encodeURIComponent(state.value) + (fips ? "&county=" + encodeURIComponent(fips) : "");
      try {
        const response = await fetch(city.dataset.source + query);
        if (!response.ok || mine !== request) return;
        const names = await response.json();
        if (mine !== request) return;
        for (const name of names) {
          const option = document.createElement("option");
          option.value = name;
          option.textContent = name;
          city.append(option);
        }
        city.disabled = city.length <= 1;
      } catch {
        // Leave the list empty; the other filters still work.
      }
    };
    state.addEventListener("change", loadCities);
    if (county) county.addEventListener("change", loadCities);
  }

  // Radius only makes sense with a ZIP.
  const zip = document.getElementById("zip");
  const radius = document.getElementById("radius");
  if (zip && radius) {
    const syncRadius = () => { radius.disabled = !/^\d{5}$/.test(zip.value); };
    zip.addEventListener("input", syncRadius);
    syncRadius();
  }

  // Don't send empty fields: keeps result URLs short and shareable.
  document.getElementById("search-form")?.addEventListener("submit", (e) => {
    if (e.defaultPrevented) return; // the map page searches in place
    for (const el of e.target.elements) {
      if (el.name && !el.value) el.disabled = true;
    }
  });
})();
