// Site shell (docs/PRODUCT_PLAN.md step 1.2): on narrow screens the sidebar's menu folds behind a button.
// The folding is CSS (html.js, set in the page head, so nothing moves after the first paint); this only opens and closes it.
// Without JavaScript the menu simply stays open and the button stays hidden.
(() => {
  const toggle = document.querySelector(".menu-toggle");
  const nav = document.getElementById("site-nav");
  if (!toggle || !nav) return;
  toggle.addEventListener("click", () => {
    const open = toggle.getAttribute("aria-expanded") !== "true";
    toggle.setAttribute("aria-expanded", open ? "true" : "false");
    nav.classList.toggle("open", open);
  });
})();
