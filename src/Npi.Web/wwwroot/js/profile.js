// Provider profile (docs/PRODUCT_PLAN.md step 1.4): "Copy NPI" puts the NPI on the clipboard.
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
