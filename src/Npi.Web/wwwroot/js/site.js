// Site shell (docs/PRODUCT_PLAN.md step 1.2): on narrow screens the sidebar's menu folds behind a button.
// Without JavaScript the menu simply stays open.
(() => {
  const toggle = document.querySelector(".menu-toggle");
  const nav = document.getElementById("site-nav");
  if (!toggle || !nav) return;
  const narrow = window.matchMedia("(max-width: 60rem)");
  const apply = () => {
    toggle.hidden = !narrow.matches;
    const open = !narrow.matches || toggle.getAttribute("aria-expanded") === "true";
    nav.classList.toggle("closed", !open);
  };
  toggle.addEventListener("click", () => {
    toggle.setAttribute("aria-expanded", toggle.getAttribute("aria-expanded") === "true" ? "false" : "true");
    apply();
  });
  narrow.addEventListener("change", apply);
  apply();
})();
