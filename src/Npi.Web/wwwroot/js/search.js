// Dependent dropdowns: Classification → Specialization, State → County.
// Works without JavaScript too: the server fills the dependent list after a search.
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

  // Radius only makes sense with a ZIP.
  const zip = document.getElementById("zip");
  const radius = document.getElementById("radius");
  const syncRadius = () => { radius.disabled = !/^\d{5}$/.test(zip.value); };
  zip.addEventListener("input", syncRadius);
  syncRadius();

  // Don't send empty fields: keeps result URLs short and shareable.
  document.getElementById("search-form").addEventListener("submit", (e) => {
    for (const el of e.target.elements) {
      if (el.name && !el.value) el.disabled = true;
    }
  });
})();
