(() => {
  const rootHost = document.getElementById("scenario-root");

  function appendChildren(element, declaration) {
    for (const child of declaration.children) {
      element.appendChild(createControl(child));
    }
  }

  function applyMetadata(element, declaration) {
    element.id = `vc-${declaration.path.replaceAll("/", "-")}`;
    element.dataset.scenarioPath = declaration.path;
    if (declaration.key) element.dataset.scenarioKey = declaration.key;
    if (declaration.isCore) element.dataset.scenarioCore = "true";
    if (declaration.name && !element.getAttribute("aria-label")) {
      element.setAttribute("aria-label", declaration.name);
    }
    if ((declaration.states & 2) !== 0) element.setAttribute("disabled", "");
    return element;
  }

  function createContainer(declaration, role) {
    const element = document.createElement("div");
    element.className = "container";
    if (declaration.kind === "HorizontalStack") element.classList.add("horizontal");
    if (role) element.setAttribute("role", role);
    appendChildren(element, declaration);
    return applyMetadata(element, declaration);
  }

  function appendVirtualItems(layer, declaration) {
    layer.replaceChildren();
    for (const child of declaration.children) {
      layer.appendChild(createControl(child));
    }
  }

  function createVirtualList(declaration) {
    const element = document.createElement("div");
    element.className = "virtual-list";
    element.setAttribute("role", "list");
    element.setAttribute("aria-setsize", String(declaration.childCount));
    element.dataset.virtualPath = declaration.path;
    element.dataset.virtualStart = "0";

    const summary = document.createElement("div");
    summary.className = "virtual-summary";
    summary.textContent = `${declaration.childCount} logical items; ${declaration.children.length} currently realized`;

    const canvas = document.createElement("div");
    canvas.className = "virtual-canvas";
    canvas.style.height = `${Math.max(1, declaration.childCount) * 32}px`;
    const layer = document.createElement("div");
    layer.className = "virtual-items";
    appendVirtualItems(layer, declaration);
    canvas.appendChild(layer);
    element.append(summary, canvas);

    element.addEventListener("scroll", () => {
      const start = Math.max(0, Math.floor(element.scrollTop / 32) - 5);
      if (start === Number(element.dataset.virtualStart)) return;
      element.dataset.virtualStart = String(start);
      CefSharp.PostMessage(JSON.stringify({
        kind: "virtualPage",
        path: declaration.path,
        start,
        count: 40
      }));
    });

    return applyMetadata(element, declaration);
  }

  function createControl(declaration) {
    let element;
    switch (declaration.kind) {
      case "Text":
        element = document.createElement("span");
        element.textContent = declaration.text ?? "";
        break;
      case "Document":
        element = document.createElement("article");
        element.contentEditable = (declaration.states & 16) === 0 ? "true" : "false";
        if (declaration.text) element.appendChild(document.createTextNode(declaration.text));
        appendChildren(element, declaration);
        break;
      case "Image":
        element = document.createElement("div");
        element.setAttribute("role", "img");
        element.textContent = declaration.name ?? "Image";
        break;
      case "Button":
        element = document.createElement("button");
        element.textContent = declaration.name ?? "Button";
        break;
      case "Link":
        element = document.createElement("a");
        element.href = "#";
        element.textContent = declaration.name ?? "Link";
        break;
      case "TextBox":
        element = document.createElement("input");
        element.type = "text";
        element.value = declaration.text ?? "";
        break;
      case "CheckBox":
      case "RadioButton": {
        element = document.createElement("label");
        const input = document.createElement("input");
        input.type = declaration.kind === "CheckBox" ? "checkbox" : "radio";
        element.append(input, document.createTextNode(declaration.name ?? declaration.kind));
        break;
      }
      case "ComboBox":
        element = document.createElement("select");
        for (const child of declaration.children) {
          const option = document.createElement("option");
          option.textContent = child.name ?? child.text ?? child.kind;
          element.appendChild(option);
        }
        break;
      case "Slider":
        element = document.createElement("input");
        element.type = "range";
        break;
      case "ProgressBar":
        element = document.createElement("progress");
        element.max = 100;
        element.value = declaration.progressValue ?? 0;
        element.textContent = `${element.value}%`;
        break;
      case "VirtualList":
        element = createVirtualList(declaration);
        break;
      case "Tree":
        element = createContainer(declaration, "tree");
        break;
      case "Table":
        element = createContainer(declaration, "grid");
        break;
      case "MenuBar":
        element = createContainer(declaration, "menubar");
        break;
      case "MenuItem": {
        element = document.createElement("div");
        element.setAttribute("role", "menuitem");
        const label = document.createElement("button");
        label.textContent = declaration.name ?? "Menu item";
        element.appendChild(label);
        if (declaration.children.length > 0) {
          element.setAttribute("aria-haspopup", "menu");
          const submenu = document.createElement("div");
          submenu.setAttribute("role", "menu");
          appendChildren(submenu, declaration);
          element.appendChild(submenu);
        }
        break;
      }
      case "Separator":
        element = document.createElement("hr");
        element.setAttribute("role", "separator");
        break;
      case "TabControl":
        element = createContainer(declaration, "tablist");
        break;
      case "TabItem": {
        element = document.createElement("section");
        element.setAttribute("role", "tabpanel");
        const tab = document.createElement("button");
        tab.setAttribute("role", "tab");
        tab.textContent = declaration.name ?? "Tab";
        element.appendChild(tab);
        appendChildren(element, declaration);
        break;
      }
      default:
        element = createContainer(declaration);
        break;
    }

    return applyMetadata(element, declaration);
  }

  globalThis.everywhere = {
    render(declaration, step) {
      rootHost.dataset.scenarioStep = String(step);
      rootHost.replaceChildren(createControl(declaration));
      return true;
    },
    updateVirtualPage(path, start, children, childCount) {
      const element = Array.from(document.querySelectorAll("[data-virtual-path]"))
        .find(candidate => candidate.dataset.virtualPath === path);
      if (!element) return false;

      const layer = element.querySelector(":scope > .virtual-canvas > .virtual-items");
      layer.style.transform = `translateY(${start * 32}px)`;
      appendVirtualItems(layer, { children });
      element.dataset.virtualStart = String(start);
      element.querySelector(":scope > .virtual-summary").textContent =
        `${childCount} logical items; ${children.length} currently realized from ${start}`;
      return true;
    }
  };
})();
