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
  const form = document.getElementById("search-form");
  form.addEventListener("submit", (e) => {
    for (const el of e.target.elements) {
      if (el.name && !el.value) el.disabled = true;
    }
  });

  // "Near me" (Stage 5.5 item 10): the browser's location → the nearest ZIP → a radius search. Browsers offer
  // geolocation only on secure pages (https or localhost), so the button stays hidden elsewhere.
  const nearMe = document.getElementById("near-me");
  if (nearMe && "geolocation" in navigator && window.isSecureContext) {
    const status = document.getElementById("near-me-status");
    const fail = (message) => {
      status.textContent = message;
      nearMe.disabled = false;
    };
    document.getElementById("near-me-field").hidden = false;
    nearMe.addEventListener("click", () => {
      nearMe.disabled = true;
      status.textContent = "Finding your location…";
      navigator.geolocation.getCurrentPosition(async (position) => {
        try {
          // Rounded to about 1 km: enough to pick a ZIP, so the exact position never leaves the browser.
          const round = (value) => Math.round(value * 100) / 100;
          const response = await fetch(nearMe.dataset.source, {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ lat: round(position.coords.latitude), lon: round(position.coords.longitude) }),
          });
          const body = await response.json();
          if (!response.ok) {
            fail(body.message || "Couldn't find a ZIP code near you.");
            return;
          }
          for (const id of ["state", "county", "city"]) document.getElementById(id).value = "";
          zip.value = body.zip;
          syncRadius();
          if (!radius.value) radius.value = "10";
          status.textContent = `Searching near ZIP ${body.zip}…`;
          form.requestSubmit();
        } catch {
          fail("Couldn't find a ZIP code near you.");
        }
      }, (error) => fail(error.code === error.PERMISSION_DENIED
        ? "Location access was blocked. Allow it in your browser, or enter a ZIP."
        : "Your location isn't available right now. Enter a ZIP instead."),
      { enableHighAccuracy: false, timeout: 15000, maximumAge: 600000 });
    });
  }
})();
