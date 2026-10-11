// Type-ahead for the search boxes (docs/PRODUCT_PLAN.md step 1.3): inputs with data-suggest get a list of specialties,
// places and providers from /suggest as you type. Arrow keys move, Enter picks, Escape closes. A provider opens its page;
// a specialty or place completes the box. Without JavaScript the boxes are plain search fields.
(() => {
  let counter = 0;
  for (const input of document.querySelectorAll("input[data-suggest]")) {
    const id = "suggest-" + ++counter;
    const list = document.createElement("ul");
    list.id = id;
    list.className = "combo-list suggest-list";
    list.setAttribute("role", "listbox");
    list.hidden = true;
    input.parentElement.style.position = "relative";
    input.parentElement.appendChild(list);
    input.setAttribute("role", "combobox");
    input.setAttribute("aria-autocomplete", "list");
    input.setAttribute("aria-expanded", "false");
    input.setAttribute("aria-controls", id);

    let items = [];
    let active = -1;
    let timer = 0;
    let request = 0;

    const close = () => {
      list.hidden = true;
      input.setAttribute("aria-expanded", "false");
      input.removeAttribute("aria-activedescendant");
      active = -1;
    };

    const setActive = (index) => {
      const nodes = list.children;
      if (active >= 0 && nodes[active]) nodes[active].setAttribute("aria-selected", "false");
      active = index;
      if (active >= 0 && nodes[active]) {
        nodes[active].setAttribute("aria-selected", "true");
        input.setAttribute("aria-activedescendant", nodes[active].id);
      } else {
        input.removeAttribute("aria-activedescendant");
      }
    };

    const pick = (item) => {
      if (item.url) {
        window.location.href = item.url;
        return;
      }
      input.value = item.text;
      close();
      input.focus();
      input.setSelectionRange(input.value.length, input.value.length);
      if (!/\s(in|near|around)\s$/i.test(item.text)) input.form.requestSubmit();
    };

    const render = () => {
      list.replaceChildren();
      items.forEach((item, i) => {
        const li = document.createElement("li");
        li.id = id + "-" + i;
        li.setAttribute("role", "option");
        li.setAttribute("aria-selected", "false");
        const label = document.createElement("span");
        label.textContent = item.label;
        const detail = document.createElement("span");
        detail.className = "muted";
        detail.textContent = item.detail || item.kind;
        li.append(label, detail);
        li.addEventListener("mousedown", (e) => {
          e.preventDefault();
          pick(item);
        });
        list.appendChild(li);
      });
      list.hidden = items.length === 0;
      input.setAttribute("aria-expanded", items.length > 0 ? "true" : "false");
      active = -1;
    };

    input.addEventListener("input", () => {
      clearTimeout(timer);
      const text = input.value;
      if (text.trim().length < 2) {
        items = [];
        render();
        return;
      }
      timer = setTimeout(async () => {
        const mine = ++request;
        try {
          const response = await fetch("/suggest?q=" + encodeURIComponent(text));
          if (!response.ok || mine !== request) return;
          items = await response.json();
          if (mine === request) render();
        } catch {
          // No suggestions; the box still searches.
        }
      }, 150);
    });

    input.addEventListener("keydown", (e) => {
      if (list.hidden) return;
      if (e.key === "ArrowDown") {
        e.preventDefault();
        setActive(Math.min(active + 1, items.length - 1));
      } else if (e.key === "ArrowUp") {
        e.preventDefault();
        setActive(Math.max(active - 1, -1));
      } else if (e.key === "Enter" && active >= 0) {
        e.preventDefault();
        pick(items[active]);
      } else if (e.key === "Escape") {
        close();
      }
    });

    input.addEventListener("blur", () => setTimeout(close, 100));
  }
})();
