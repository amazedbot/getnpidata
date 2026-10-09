// Type-to-filter combo box (Stage 5.5 item 12): a text input whose list shows every option when opened and narrows to
// the options that START with what is typed, ignoring case and punctuation ("pa-" finds PA-C). Arrow keys move,
// Enter picks, Escape closes. The input keeps working as a plain text box without JavaScript.
(() => {
  const key = (s) => s.toUpperCase().replace(/[^A-Z0-9]/g, "");

  for (const input of document.querySelectorAll("input[data-combo-options]")) {
    const list = document.getElementById(input.getAttribute("aria-controls"));
    let options;
    try {
      options = JSON.parse(document.getElementById(input.dataset.comboOptions).textContent).map(([value, count]) => ({ value, count, key: key(value) }));
    } catch {
      continue;
    }

    let shown = [];
    let active = -1;
    const format = new Intl.NumberFormat();

    const close = () => {
      list.hidden = true;
      input.setAttribute("aria-expanded", "false");
      input.removeAttribute("aria-activedescendant");
      active = -1;
    };

    const setActive = (index) => {
      const items = list.children;
      if (active >= 0 && items[active]) items[active].setAttribute("aria-selected", "false");
      active = index;
      if (active >= 0 && items[active]) {
        items[active].setAttribute("aria-selected", "true");
        items[active].scrollIntoView({ block: "nearest" });
        input.setAttribute("aria-activedescendant", items[active].id);
      } else {
        input.removeAttribute("aria-activedescendant");
      }
    };

    const pick = (option) => {
      input.value = option.value;
      close();
      input.dispatchEvent(new Event("change", { bubbles: true }));
    };

    const open = (filter) => {
      const typed = key(filter);
      shown = typed ? options.filter((o) => o.key.startsWith(typed)) : options;
      list.replaceChildren();
      shown.forEach((option, i) => {
        const li = document.createElement("li");
        li.id = `${list.id}-${i}`;
        li.setAttribute("role", "option");
        li.setAttribute("aria-selected", "false");
        const name = document.createElement("span");
        name.textContent = option.value;
        const count = document.createElement("span");
        count.className = "muted";
        count.textContent = format.format(option.count);
        li.append(name, count);
        li.addEventListener("mousedown", (e) => {
          e.preventDefault(); // keep focus in the input
          pick(option);
        });
        list.append(li);
      });
      if (shown.length === 0) {
        const li = document.createElement("li");
        li.className = "empty";
        li.textContent = "No listed credential starts with that; it will be searched as typed.";
        list.append(li);
      }
      list.hidden = false;
      // Near the right edge of the page, open towards the left so the counts stay visible.
      list.classList.remove("align-right");
      if (list.getBoundingClientRect().right > document.documentElement.clientWidth) list.classList.add("align-right");
      input.setAttribute("aria-expanded", "true");
      active = -1;
    };

    // Opening on focus shows everything; typing narrows it.
    input.addEventListener("focus", () => open(""));
    input.addEventListener("click", () => { if (list.hidden) open(""); });
    input.addEventListener("input", () => open(input.value));
    input.addEventListener("blur", close);
    input.addEventListener("keydown", (e) => {
      if (e.key === "ArrowDown" || e.key === "ArrowUp") {
        e.preventDefault();
        if (list.hidden) open(input.value);
        if (shown.length === 0) return;
        const next = e.key === "ArrowDown" ? Math.min(active + 1, shown.length - 1) : Math.max(active - 1, 0);
        setActive(next);
      } else if (e.key === "Enter" && !list.hidden && active >= 0 && shown[active]) {
        e.preventDefault(); // pick, don't submit yet
        pick(shown[active]);
      } else if (e.key === "Escape" && !list.hidden) {
        e.preventDefault();
        close();
      }
    });
  }
})();
