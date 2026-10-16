// Provider profile (docs/PRODUCT_PLAN.md step 1.4): "Copy NPI" puts the NPI on the clipboard. An organization's page (step 3.9)
// prints as one document: its folded lists open for printing and fold again afterwards.
(() => {
  for (const button of document.querySelectorAll("button[data-copy]")) {
    const label = button.textContent;
    button.addEventListener("click", async () => {
      try {
        await navigator.clipboard.writeText(button.dataset.copy);
        button.textContent = "Copied";
      } catch {
        button.textContent = button.dataset.copy; // no clipboard access: show it to copy by hand
      }
      setTimeout(() => { button.textContent = label; }, 2000);
    });
  }
})();
(() => {
  let opened = [];
  addEventListener("beforeprint", () => {
    opened = [...document.querySelectorAll("details:not([open])")];
    for (const d of opened) d.open = true;
  });
  addEventListener("afterprint", () => {
    for (const d of opened) d.open = false;
    opened = [];
  });
})();
