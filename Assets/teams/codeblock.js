// The composer's code blocks: a language button, a "..." menu, line numbers, and a clear textarea over highlight.js's colored copy.
"use strict";
(() => {
  // The dropdown's languages, in order; also the names the thread's code cards show.
  const Languages = [["plaintext", "Plain Text"], ["json", "JSON"], ["sql", "SQL"], ["csharp", "C#"]];
  const LanguageNames = new Map(Languages);
  const Icons = { chevronDown: "\uF2A3", more: "\uE824", checkmark: "\uF294", delete: "\uF34C" };
  // The host's rules (TeamsCompose.CodeBlockClass and its itemid pattern), checked again there.
  const LanguageToken = /^language-([a-z0-9+#_-]{1,24})$/;
  const ManualToken = "language-was-manually-selected";
  const ItemIdPattern = /^codeBlockEditor-[0-9a-f-]{36}$/;
  const StorageKey = "teamsCodeLanguage";
  const MaxVisibleLines = 16;
  const LineHeight = 20;
  const Padding = 8;
  const ScrollBarSize = 14;
  const Indent = "    ";
  const LineBlockTags = new Set(["P", "DIV", "LI", "PRE", "BLOCKQUOTE", "UL", "OL"]);

  const escapeHtml = (text) => text.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;").replace(/"/g, "&quot;");

  const createElement = (tag, className, text) => {
    const element = document.createElement(tag);
    if (className) element.className = className;
    if (text) element.textContent = text;
    return element;
  };

  const languageName = (id) => {
    if (LanguageNames.has(id)) return LanguageNames.get(id);
    const known = window.hljs.getLanguage(id);
    return known ? known.name : id;
  };

  // Only the listed languages are colored; any other id keeps its name but shows and goes out as plain text.
  const highlightHtml = (text, language) => LanguageNames.has(language) && language !== "plaintext"
    ? window.hljs.highlight(text, { language, ignoreIllegals: true }).value
    : escapeHtml(text);

  // One language-<id> plus at most the manual flag, else null: the class the host keeps.
  const parseClass = (className) => {
    const tokens = (className || "").split(/\s+/).filter((token) => token !== "");
    const languageTokens = tokens.filter((token) => token !== ManualToken);
    const manualCount = tokens.length - languageTokens.length;
    const match = languageTokens.length === 1 && manualCount <= 1 ? LanguageToken.exec(languageTokens[0]) : null;
    if (!match || match[1] === "was-manually-selected") return null;
    return { language: match[1], manual: manualCount === 1 };
  };

  const savedLanguage = () => {
    try {
      const id = window.localStorage.getItem(StorageKey);
      return LanguageNames.has(id) ? id : "plaintext";
    }
    catch {
      return "plaintext";
    }
  };

  const saveLanguage = (id) => {
    try {
      window.localStorage.setItem(StorageKey, id);
    }
    catch {
      // No storage: the next block starts as Plain Text, nothing else is lost.
    }
  };

  const isBlock = (node) => node !== null && node.nodeType === Node.ELEMENT_NODE && node.classList.contains("code-block");

  const isInside = (node) => {
    const element = node && node.nodeType === Node.ELEMENT_NODE ? node : node ? node.parentElement : null;
    return element !== null && element.closest(".code-block") !== null;
  };

  const textOf = (block) => block.querySelector(".code-text").value;

  // Text with its line breaks: a br or a block's end is "\n", a non-breaking space a space; images and frames add nothing.
  const textWithBreaks = (root) => {
    let text = "";
    const endLine = () => {
      if (text !== "" && !text.endsWith("\n")) text += "\n";
    };
    const walk = (node) => {
      for (const child of node.childNodes) {
        if (child.nodeType === Node.TEXT_NODE) text += child.data;
        else if (child.nodeType !== Node.ELEMENT_NODE || child.classList.contains("image-block")) continue;
        else if (child.tagName === "BR") text += "\n";
        else if (isBlock(child)) {
          endLine();
          text += textOf(child);
          endLine();
        }
        else {
          const isLineBlock = LineBlockTags.has(child.tagName);
          if (isLineBlock) endLine();
          walk(child);
          if (isLineBlock) endLine();
        }
      }
    };
    walk(root);
    return text.replace(/\u00a0/g, " ").replace(/\n$/, "");
  };

  // The open menu's close, shared by every composer: only one menu is open at a time.
  let closeOpenMenu = null;
  document.addEventListener("mousedown", (e) => {
    if (closeOpenMenu) closeOpenMenu(e.target);
  });
  // Fixed to the window, so it would float away from its button when anything under it scrolls.
  window.addEventListener("scroll", (e) => {
    if (closeOpenMenu) closeOpenMenu(e.target);
  }, true);

  // options: editor, popupHost (the composer box), inertContent (the composer's HTML sink), submit, changed, isBusy, getLabels.
  const createCodeBlocks = (options) => {
    const editor = options.editor;
    const menu = createElement("div", "menu code-menu");
    menu.setAttribute("role", "menu");
    menu.hidden = true;
    options.popupHost.append(menu);
    let menuButton = null;
    menu.addEventListener("keydown", (e) => {
      if (e.key !== "Escape" || menuButton === null) return;
      e.preventDefault();
      e.stopPropagation();
      const button = menuButton;
      closeMenu();
      button.focus();
    });

    const partsOf = (block) => ({
      name: block.querySelector(".code-language-name"),
      gutter: block.querySelector(".code-gutter"),
      area: block.querySelector(".code-area"),
      colored: block.querySelector(".code-colored"),
      textarea: block.querySelector(".code-text"),
      buttons: [...block.querySelectorAll(".code-head .btn")],
    });

    const syncScroll = (parts) => {
      parts.colored.scrollTop = parts.textarea.scrollTop;
      parts.colored.scrollLeft = parts.textarea.scrollLeft;
      parts.gutter.scrollTop = parts.textarea.scrollTop;
    };

    // The trailing "\n" gives the pre the textarea's empty last line, so both are the same height.
    const render = (block) => {
      const parts = partsOf(block);
      const text = parts.textarea.value;
      parts.colored.replaceChildren(options.inertContent(highlightHtml(text, block.dataset.language) + "\n"));
      const lineCount = text.split("\n").length;
      parts.gutter.textContent = Array.from({ length: lineCount }, (_, index) => String(index + 1)).join("\n");
      parts.name.textContent = languageName(block.dataset.language);
      const hasHorizontalBar = parts.textarea.scrollWidth > parts.textarea.clientWidth;
      const height = Math.min(lineCount, MaxVisibleLines) * LineHeight + 2 * Padding + (hasHorizontalBar ? ScrollBarSize : 0);
      parts.area.style.height = height + "px";
      parts.gutter.style.height = height + "px";
      syncScroll(parts);
    };

    const applyLabels = (block) => {
      const labels = options.getLabels();
      const parts = partsOf(block);
      parts.buttons[0].title = labels.codeLanguage;
      parts.buttons[0].setAttribute("aria-label", labels.codeLanguage);
      parts.buttons[1].title = labels.deleteCodeBlock;
      parts.buttons[1].setAttribute("aria-label", labels.deleteCodeBlock);
    };

    const focusText = (block, atEnd) => {
      const textarea = partsOf(block).textarea;
      textarea.focus();
      const position = atEnd ? textarea.value.length : 0;
      textarea.setSelectionRange(position, position);
    };

    const placeCaret = (element, atEnd) => {
      editor.focus();
      const range = document.createRange();
      range.selectNodeContents(element);
      range.collapse(!atEnd);
      const selection = window.getSelection();
      selection.removeAllRanges();
      selection.addRange(range);
    };

    const emptyParagraph = () => {
      const paragraph = document.createElement("p");
      paragraph.append(document.createElement("br"));
      return paragraph;
    };

    // Into what is beside the block: another block's text, or a paragraph's start / end; past the last block, a new paragraph.
    const moveOut = (block, forward) => {
      let sibling = forward ? block.nextElementSibling : block.previousElementSibling;
      if (!sibling && !forward) return false;
      if (!sibling) {
        sibling = emptyParagraph();
        block.after(sibling);
      }
      if (isBlock(sibling)) focusText(sibling, !forward);
      else placeCaret(sibling, !forward);
      return true;
    };

    const remove = (block) => {
      const previous = block.previousElementSibling;
      const next = block.nextElementSibling;
      block.remove();
      if (previous && isBlock(previous)) focusText(previous, true);
      else if (previous) placeCaret(previous, true);
      else if (next && isBlock(next)) focusText(next, false);
      else if (next) placeCaret(next, false);
      else editor.focus();
      options.changed();
    };

    // A closed menu returns at once, so it never clears another composer's open one.
    const closeMenu = () => {
      if (menu.hidden) return;
      menu.hidden = true;
      menu.replaceChildren();
      if (menuButton) menuButton.setAttribute("aria-expanded", "false");
      menuButton = null;
      closeOpenMenu = null;
    };

    const addMenuItem = (glyph, text, action) => {
      const item = createElement("button", "menu-item");
      item.type = "button";
      item.setAttribute("role", "menuitem");
      item.append(createElement("span", "glyph check", glyph), createElement("span", "menu-text", text));
      item.addEventListener("click", action);
      menu.append(item);
    };

    const pickLanguage = (block, id) => {
      closeMenu();
      block.dataset.language = id;
      block.dataset.manual = "1";
      saveLanguage(id);
      render(block);
      partsOf(block).textarea.focus();
      options.changed();
    };

    const fillLanguages = (block) => {
      for (const [id, name] of Languages) addMenuItem(id === block.dataset.language ? Icons.checkmark : "", name, () => pickLanguage(block, id));
    };

    const fillMore = (block) => addMenuItem(Icons.delete, options.getLabels().deleteCodeBlock, () => {
      closeMenu();
      remove(block);
    });

    // Fixed to the window, so the editor's scroll box does not clip it; above the button when there is more room there.
    const openMenu = (block, button, fill) => {
      const wasOpenHere = menuButton === button;
      if (closeOpenMenu) closeOpenMenu(null);
      if (wasOpenHere || options.isBusy()) return;
      menuButton = button;
      button.setAttribute("aria-expanded", "true");
      fill(block);
      menu.hidden = false;
      const buttonBox = button.getBoundingClientRect();
      const roomBelow = window.innerHeight - buttonBox.bottom - 12;
      const roomAbove = buttonBox.top - 12;
      const opensBelow = roomBelow >= Math.min(menu.scrollHeight, 240) || roomBelow >= roomAbove;
      menu.style.maxHeight = Math.max(80, opensBelow ? roomBelow : roomAbove) + "px";
      menu.style.left = Math.max(4, Math.min(buttonBox.left, window.innerWidth - menu.offsetWidth - 4)) + "px";
      menu.style.top = opensBelow ? buttonBox.bottom + 4 + "px" : "";
      menu.style.bottom = opensBelow ? "" : window.innerHeight - buttonBox.top + 4 + "px";
      closeOpenMenu = (target) => {
        if (target === null || (!menu.contains(target) && !button.contains(target))) closeMenu();
      };
      const checked = menu.querySelector(".check:not(:empty)");
      (checked ? checked.parentElement : menu.firstElementChild).focus();
    };

    // Through insertText, so Ctrl+Z undoes it like typing; several lines get 4 spaces more, or up to 4 fewer, each.
    const indent = (textarea, outdent) => {
      const { selectionStart: start, selectionEnd: end, value } = textarea;
      if (!outdent && !value.slice(start, end).includes("\n")) {
        document.execCommand("insertText", false, Indent);
        return;
      }
      const lineStart = value.lastIndexOf("\n", start - 1) + 1;
      const lines = value.slice(lineStart, end);
      const changedLines = lines.split("\n").map((line) => outdent ? line.replace(/^ {1,4}/, "") : Indent + line).join("\n");
      if (changedLines === lines) return;
      textarea.setSelectionRange(lineStart, end);
      document.execCommand("insertText", false, changedLines);
      if (start !== end) textarea.setSelectionRange(lineStart, lineStart + changedLines.length);
      else {
        const caret = Math.max(lineStart, start + changedLines.length - lines.length);
        textarea.setSelectionRange(caret, caret);
      }
    };

    const onKeyDown = (block, e) => {
      if (options.isBusy() || e.isComposing) return;
      const textarea = e.target;
      const { selectionStart: start, selectionEnd: end, value } = textarea;
      const isCollapsed = start === end;
      if (e.key === "Enter" && e.ctrlKey) {
        e.preventDefault();
        options.submit();
      }
      else if (e.key === "Tab" && !e.ctrlKey && !e.altKey) {
        e.preventDefault();
        indent(textarea, e.shiftKey);
      }
      else if (e.key === "Escape") {
        e.preventDefault();
        moveOut(block, true);
      }
      else if (e.key === "ArrowUp" && isCollapsed && !e.shiftKey && !value.slice(0, start).includes("\n")) {
        if (moveOut(block, false)) e.preventDefault();
      }
      else if (e.key === "ArrowDown" && isCollapsed && !e.shiftKey && !value.slice(end).includes("\n")) {
        e.preventDefault();
        moveOut(block, true);
      }
      else if (e.key === "Backspace" && value === "") {
        e.preventDefault();
        remove(block);
      }
    };

    // language and itemId as the message had them; a new block takes the last language picked and a new id.
    const create = (text, language, manual, itemId) => {
      const block = createElement("div", "code-block");
      block.contentEditable = "false";
      block.dataset.language = language || savedLanguage();
      block.dataset.itemId = itemId || "codeBlockEditor-" + crypto.randomUUID();
      if (manual) block.dataset.manual = "1";
      const head = createElement("div", "code-head");
      const languageButton = createElement("button", "btn ghost code-language");
      languageButton.type = "button";
      languageButton.setAttribute("aria-haspopup", "true");
      languageButton.append(createElement("span", "code-language-name"), createElement("span", "glyph", Icons.chevronDown));
      const moreButton = createElement("button", "btn ghost icon glyph code-more", Icons.more);
      moreButton.type = "button";
      moreButton.setAttribute("aria-haspopup", "true");
      head.append(languageButton, createElement("span", "code-head-fill"), moreButton);
      const body = createElement("div", "code-body");
      const gutter = createElement("div", "code-gutter");
      gutter.setAttribute("aria-hidden", "true");
      const area = createElement("div", "code-area");
      const colored = createElement("pre", "code-colored");
      colored.setAttribute("aria-hidden", "true");
      const textarea = createElement("textarea", "code-text");
      textarea.spellcheck = false;
      textarea.wrap = "off";
      textarea.value = text || "";
      area.append(colored, textarea);
      body.append(gutter, area);
      block.append(head, body);
      // The block is its own text field: the editor's Enter-to-send, shortcuts, tag popup and paste cleaning stay out.
      for (const type of ["keydown", "input", "paste"]) block.addEventListener(type, (e) => e.stopPropagation());
      textarea.addEventListener("keydown", (e) => onKeyDown(block, e));
      textarea.addEventListener("input", () => {
        render(block);
        options.changed();
      });
      textarea.addEventListener("compositionend", () => render(block));
      textarea.addEventListener("scroll", () => syncScroll(partsOf(block)));
      languageButton.addEventListener("click", () => openMenu(block, languageButton, fillLanguages));
      moreButton.addEventListener("click", () => openMenu(block, moreButton, fillMore));
      applyLabels(block);
      render(block);
      return block;
    };

    // A pre the host kept (or a pasted one): its language and flag from the class, else Plain Text; its text with the line breaks.
    const fromPre = (pre) => {
      const parsed = parseClass(pre.getAttribute("class"));
      const itemId = (pre.getAttribute("itemid") || "").trim();
      return create(textWithBreaks(pre), parsed ? parsed.language : "plaintext", parsed !== null && parsed.manual,
        ItemIdPattern.test(itemId) ? itemId : null);
    };

    // Puts a block nested in one of container's children beside that child: a paragraph is split around it, a list or quote goes before it.
    const lift = (block, container) => {
      let top = block;
      while (top.parentNode !== container) top = top.parentNode;
      if (top === block) return;
      if (top.tagName !== "P" && top.tagName !== "DIV") {
        top.after(block);
        return;
      }
      const tailRange = document.createRange();
      tailRange.setStartAfter(block);
      tailRange.setEnd(top, top.childNodes.length);
      const tail = top.cloneNode(false);
      tail.append(tailRange.extractContents());
      top.after(block, tail);
      for (const half of [top, tail]) {
        if (half.textContent === "" && !half.querySelector(".chip, .image-block, .code-block")) half.remove();
      }
    };

    // Teams keeps a code block only at the top level; last first, so two blocks lifted out of one list keep their order.
    const liftAll = (container) => {
      for (const block of [...container.querySelectorAll(".code-block")].reverse()) lift(block, container);
    };

    // At the range, out of any paragraph, list or quote; an empty paragraph after it when nothing follows, to type on.
    const insert = (range, text) => {
      const block = create(text);
      range.insertNode(block);
      lift(block, editor);
      if (!block.nextSibling) block.after(emptyParagraph());
      render(block);
      focusText(block, true);
      return block;
    };

    // ArrowDown at the end of the paragraph before a block, or ArrowUp at the start of the one after it, enters the block's text.
    const enterFrom = (key) => {
      const selection = window.getSelection();
      if (selection.rangeCount === 0 || !selection.isCollapsed) return false;
      const caret = selection.getRangeAt(0);
      let top = caret.startContainer;
      while (top && top.parentNode !== editor) top = top.parentNode;
      if (!top) return false;
      const forward = key === "ArrowDown";
      const sibling = forward ? top.nextElementSibling : top.previousElementSibling;
      if (!isBlock(sibling)) return false;
      const rest = document.createRange();
      rest.selectNodeContents(top);
      if (forward) rest.setStart(caret.startContainer, caret.startOffset);
      else rest.setEnd(caret.startContainer, caret.startOffset);
      if (rest.toString() !== "" || rest.cloneContents().querySelector(".chip, .image-block")) return false;
      focusText(sibling, !forward);
      return true;
    };

    // The editor's text without the blocks' frames, plus what is typed in each block.
    const editorText = () => {
      const walker = document.createTreeWalker(editor, NodeFilter.SHOW_ELEMENT | NodeFilter.SHOW_TEXT,
        { acceptNode: (node) => isBlock(node) ? NodeFilter.FILTER_REJECT : NodeFilter.FILTER_ACCEPT });
      let text = "";
      for (let node = walker.nextNode(); node; node = walker.nextNode()) {
        if (node.nodeType === Node.TEXT_NODE) text += node.data;
      }
      for (const block of editor.querySelectorAll(".code-block")) text += textOf(block);
      return text;
    };

    // Highlighted again from the text, not read off the screen; Teams stores spaces as &nbsp; and line breaks as <br>, tags untouched.
    const serialize = (block) => {
      const code = highlightHtml(textOf(block), block.dataset.language).split(/(<[^>]*>)/)
        .map((part) => part.startsWith("<") ? part : part.replace(/ /g, "&nbsp;").replace(/\n/g, "<br>"))
        .join("");
      const className = "language-" + block.dataset.language + (block.dataset.manual ? " " + ManualToken : "");
      return '<pre class="' + escapeHtml(className) + '" itemid="' + escapeHtml(block.dataset.itemId) + '"><code>' + code + "</code></pre>";
    };

    const hasFocus = () => {
      const active = document.activeElement;
      return active !== null && (menu.contains(active) || (editor.contains(active) && isInside(active)));
    };

    const setBusy = (value) => {
      if (value) closeMenu();
      for (const block of editor.querySelectorAll(".code-block")) {
        const parts = partsOf(block);
        parts.textarea.readOnly = value;
        for (const button of parts.buttons) button.disabled = value;
      }
    };

    const setLabels = () => {
      for (const block of editor.querySelectorAll(".code-block")) applyLabels(block);
      if (!menu.hidden) closeMenu();
    };

    // Blocks built off the page measure no scroll bar; drawn again once they are in the editor.
    const refresh = () => {
      for (const block of editor.querySelectorAll(".code-block")) render(block);
    };

    return {
      isBlock, isInside, textOf, textWithBreaks, fromPre, liftAll, insert, enterFrom, editorText, serialize, hasFocus, setBusy, setLabels, refresh,
    };
  };

  window.createCodeBlocks = createCodeBlocks;
  window.codeBlockLanguageName = (pre) => {
    const parsed = parseClass(pre.getAttribute("class"));
    return parsed ? languageName(parsed.language) : null;
  };
})();
