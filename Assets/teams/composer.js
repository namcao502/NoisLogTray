// The Teams page's rich text box: chips for @mentions, image and code blocks, one DOM whitelist for host and pasted HTML, BuildBody's markers out.
"use strict";
(() => {
  const Icons = {
    bold: "\uF7A4", italic: "\uF7F4", underline: "\uF80A", strikethrough: "\uED5F", bulletList: "\uECD9", numberList: "\uF7F9",
    quote: "\uF800", link: "\uF4E4", code: "\uF2EF", image: "\uF488", mention: "\uF53C", send: "\uF699",
  };
  // The host checks these again (TeamsCompose); checked here first so a refused image never crosses over.
  const MaxImages = 6;
  const MaxImageBytes = 10 * 1024 * 1024;
  const MaxTotalImageBytes = 20 * 1024 * 1024;
  const ImageTypes = ["image/png", "image/jpeg", "image/gif"];
  const MaxMentionChoices = 8;
  const MentionQuery = /(^|\s)@([^\s@]{0,30})$/;
  // The tags TeamsCompose.BuildBody keeps (it turns div into p); every other element is unwrapped or dropped.
  const KeptTags = new Set(["P", "DIV", "BR", "STRONG", "B", "EM", "I", "U", "S", "STRIKE", "CODE", "PRE", "BLOCKQUOTE", "UL", "OL", "LI"]);
  const RenamedTags = new Map([["DEL", "S"], ["INS", "U"], ["TR", "P"]]);
  // execCommand writes b / i / strike; the message gets the semantic tags.
  const SerializedTags = new Map([["B", "strong"], ["I", "em"], ["STRIKE", "s"]]);
  const DroppedWithContent = new Set(["SCRIPT", "STYLE", "IFRAME", "OBJECT", "EMBED", "SVG", "MATH", "TEMPLATE", "HEAD", "TITLE",
    "META", "LINK", "NOSCRIPT", "BUTTON", "INPUT", "SELECT", "TEXTAREA", "IMG", "HR"]);
  const BlockTags = new Set(["P", "DIV", "PRE", "BLOCKQUOTE", "UL", "OL", "LI"]);

  const isWebUrl = (href) => /^https?:\/\/\S+$/i.test(href.trim());
  const escapeHtml = (text) => text.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;").replace(/"/g, "&quot;");
  // Lowercase, without Vietnamese marks, so "minh" finds "Minh" and "duc" finds "Đức".
  const fold = (text) => text.toLowerCase().normalize("NFD").replace(/[\u0300-\u036f]/g, "").replace(/\u0111/g, "d");
  // A function, not a string, as the replacement: a "$&" in a file name stays as it is.
  const withName = (template, name) => template.replace("{0}", () => name);

  // The one HTML sink of this file: a template's content is inert, so nothing in it runs or loads before the whitelist.
  const inertContent = (html) => {
    const template = document.createElement("template");
    template.innerHTML = html;
    return template.content;
  };

  const createElement = (tag, className, text) => {
    const element = document.createElement(tag);
    if (className) element.className = className;
    if (text) element.textContent = text;
    return element;
  };

  const createComposer = (container, options) => {
    const mode = options.mode;
    let labels = options.labels;
    let busy = false;
    let statusText = "";
    let statusKind = "";
    let savedRange = null;
    let mention = null; // { node, start, end } of the "@query" while the popup is open
    let mentionChoices = [];
    let mentionIndex = 0;

    const root = createElement("div", "composer composer-" + mode);
    const toolbar = createElement("div", "toolbar");
    const editor = createElement("div", "editor");
    editor.contentEditable = "true";
    editor.spellcheck = false;
    const mentionPopup = createElement("div", "popup mention-popup");
    mentionPopup.setAttribute("role", "listbox");
    mentionPopup.hidden = true;
    const linkPopup = createElement("div", "popup link-popup");
    linkPopup.hidden = true;
    const linkInput = createElement("input", "text");
    linkInput.type = "text";
    linkInput.spellcheck = false;
    const linkAdd = createElement("button", "btn primary");
    const linkError = createElement("div", "link-error");
    linkPopup.append(linkInput, linkAdd, linkError);
    const bottom = createElement("div", "bottom");
    const hint = createElement("span", "hint");
    const mentionButton = createElement("button", "btn icon ghost glyph", Icons.mention);
    mentionButton.dataset.label = "mention";
    const imageButton = createElement("button", "btn icon ghost glyph", Icons.image);
    imageButton.dataset.label = "addImage";
    const cancelButton = createElement("button", "btn");
    const submitButton = createElement("button", "btn primary");
    const submitText = createElement("span");
    submitButton.append(submitText);
    if (mode === "reply") submitButton.append(createElement("span", "glyph", Icons.send));
    const fileInput = createElement("input");
    fileInput.type = "file";
    fileInput.accept = ImageTypes.join(",");
    fileInput.multiple = true;
    fileInput.hidden = true;
    bottom.append(hint, mentionButton, imageButton);
    if (mode === "edit") bottom.append(cancelButton);
    bottom.append(submitButton);
    root.append(toolbar, editor, mentionPopup, linkPopup, bottom, fileInput);
    container.append(root);
    const codeBlocks = window.createCodeBlocks({
      editor, popupHost: root, inertContent, submit: () => submit(), changed: () => changed(), isBusy: () => busy, getLabels: () => labels,
    });

    const toolButtons = [];
    const addTool = (name, action) => {
      const button = createElement("button", "btn tool glyph", Icons[name]);
      button.type = "button";
      button.dataset.label = name;
      // Keeps the focus and the selection in the editor.
      button.addEventListener("mousedown", (e) => e.preventDefault());
      button.addEventListener("click", () => {
        // A code block is plain text: no formatting, links, tags or images inside it, and no block in a block.
        if (busy || codeBlocks.hasFocus()) return;
        restoreRange();
        action();
        changed();
      });
      toolbar.append(button);
      toolButtons.push(button);
    };
    const addSeparator = () => toolbar.append(createElement("span", "sep"));

    // In the editor's own text, never inside a code block's frame.
    const isEditorRange = (range) => editor.contains(range.commonAncestorContainer) && !codeBlocks.isInside(range.commonAncestorContainer);

    const currentRange = () => {
      const selection = window.getSelection();
      if (selection.rangeCount !== 0 && isEditorRange(selection.getRangeAt(0))) return selection.getRangeAt(0);
      // A saved range collapses outside the box when the box is moved, as a re-rendered thread moves an edit box.
      if (savedRange && isEditorRange(savedRange)) return savedRange;
      const range = document.createRange();
      range.selectNodeContents(editor);
      range.collapse(false);
      return range;
    };

    const selectRange = (range) => {
      const selection = window.getSelection();
      selection.removeAllRanges();
      selection.addRange(range);
    };

    // A toolbar click, the file picker or the link popup took the focus; the edit goes where the caret was.
    const restoreRange = () => {
      editor.focus();
      selectRange(currentRange());
    };

    const placeCaretAfter = (node) => {
      const range = document.createRange();
      range.setStartAfter(node);
      range.collapse(true);
      selectRange(range);
      savedRange = range.cloneRange();
    };

    // An empty code block counts as empty; its frame (language, line numbers) is not text.
    const isEmpty = () => codeBlocks.editorText().trim() === "" && !editor.querySelector(".chip, .image-block");

    // The placeholder would sit over a code block's frame, so it shows only without one.
    const showPlaceholder = () => editor.classList.toggle("is-empty", isEmpty() && !editor.querySelector(".code-block"));

    const changed = () => {
      showPlaceholder();
      if (options.onChange) options.onChange();
    };

    const renderHint = () => {
      const defaultHint = mode === "reply" ? labels.replyHint : mode === "post" ? labels.postHint : labels.editHint;
      hint.textContent = statusText || defaultHint;
      hint.className = "hint" + (statusText && statusKind ? " " + statusKind : "");
    };

    const setStatus = (text, kind) => {
      statusText = text || "";
      statusKind = kind || "";
      renderHint();
    };

    const createChip = (mri, name, text) => {
      const chip = createElement("span", "chip", text);
      chip.contentEditable = "false";
      chip.dataset.mri = mri;
      chip.dataset.name = name;
      return chip;
    };

    const createImageBlock = (image) => {
      const block = createElement("span", "image-block");
      block.contentEditable = "false";
      const removeButton = createElement("button", "image-remove", "\u00d7");
      removeButton.type = "button";
      removeButton.tabIndex = -1;
      removeButton.title = labels.removeImage;
      removeButton.setAttribute("aria-label", labels.removeImage);
      removeButton.addEventListener("click", () => {
        if (busy) return;
        block.remove();
        changed();
      });
      block.append(image, removeButton);
      return block;
    };

    // An image the edited message already has: shown through the host's image loader, sent back by its AMS id.
    const createKeptImageBlock = (source) => {
      const image = createElement("img", "ams");
      image.dataset.ams = source.dataset.ams;
      image.dataset.src = source.dataset.src || "";
      for (const side of ["width", "height"]) {
        const value = source.getAttribute(side);
        if (value && /^\d+$/.test(value)) image.setAttribute(side, value);
      }
      if (options.loadImage) options.loadImage(image);
      return createImageBlock(image);
    };

    // Copies source's children into target through the whitelist; fromHost lets the host's mention spans and AMS images in.
    const cleanChildren = (source, target, fromHost) => {
      for (const node of source.childNodes) {
        if (node.nodeType === Node.TEXT_NODE) {
          target.append(document.createTextNode(node.data));
          continue;
        }
        if (node.nodeType !== Node.ELEMENT_NODE) continue;
        const tag = node.tagName.toUpperCase();
        if (fromHost && tag === "SPAN" && node.classList.contains("mention") && node.dataset.mri) {
          target.append(createChip(node.dataset.mri, node.textContent, node.textContent));
          continue;
        }
        if (fromHost && tag === "IMG" && node.dataset.ams) {
          target.append(createKeptImageBlock(node));
          continue;
        }
        if (DroppedWithContent.has(tag)) continue;
        // Read before the whitelist: the block keeps the pre's language, flag and itemid when the host's rules allow them.
        if (tag === "PRE") {
          target.append(codeBlocks.fromPre(node));
          continue;
        }
        if (tag === "A") {
          const href = node.getAttribute("href") || "";
          if (!isWebUrl(href)) {
            cleanChildren(node, target, fromHost);
            continue;
          }
          const anchor = document.createElement("a");
          anchor.setAttribute("href", href.trim());
          cleanChildren(node, anchor, fromHost);
          target.append(anchor);
          continue;
        }
        if (/^H[1-6]$/.test(tag)) {
          const paragraph = document.createElement("p");
          const strong = document.createElement("strong");
          cleanChildren(node, strong, fromHost);
          paragraph.append(strong);
          target.append(paragraph);
          continue;
        }
        const keptTag = RenamedTags.get(tag) || tag;
        if (KeptTags.has(keptTag)) {
          const copy = document.createElement(keptTag.toLowerCase());
          cleanChildren(node, copy, fromHost);
          target.append(copy);
          continue;
        }
        cleanChildren(node, target, fromHost);
        // A table cell's text would otherwise run into the next cell's.
        if (tag === "TD" || tag === "TH") target.append(document.createTextNode(" "));
      }
    };

    const setContent = (html) => {
      const fragment = document.createDocumentFragment();
      cleanChildren(inertContent(html), fragment, true);
      codeBlocks.liftAll(fragment);
      editor.replaceChildren(fragment);
      codeBlocks.refresh();
      savedRange = null;
      closeMentionPopup();
      closeLinkPopup();
      showPlaceholder();
    };

    // The editor's child that holds the caret; null when the caret sits on the editor itself.
    const editorChild = (node) => {
      let current = node;
      while (current && current !== editor && current.parentNode !== editor) current = current.parentNode;
      return current === editor ? null : current;
    };

    const insertInline = (range, nodes) => {
      const fragment = document.createDocumentFragment();
      fragment.append(...nodes);
      const last = fragment.lastChild;
      range.insertNode(fragment);
      if (last) placeCaretAfter(last);
    };

    // Pasted blocks split the paragraph at the caret; in a list, quote or code block they become lines of text.
    const insertFragment = (fragment) => {
      restoreRange();
      const range = currentRange();
      range.deleteContents();
      const nodes = [...fragment.childNodes];
      const hasBlocks = nodes.some((node) => node.nodeType === Node.ELEMENT_NODE && BlockTags.has(node.tagName));
      const block = editorChild(range.startContainer);
      const isInBlock = block !== null && block.nodeType === Node.ELEMENT_NODE && BlockTags.has(block.tagName);
      if (!hasBlocks || !isInBlock) {
        insertInline(range, nodes);
        changed();
        return;
      }
      if (block.tagName !== "P" && block.tagName !== "DIV") {
        const lines = [];
        nodes.forEach((node, index) => {
          if (index !== 0) lines.push(document.createElement("br"));
          lines.push(document.createTextNode(codeBlocks.isBlock(node) ? codeBlocks.textOf(node) : node.textContent));
        });
        insertInline(range, lines);
        changed();
        return;
      }
      const tailRange = document.createRange();
      tailRange.setStart(range.startContainer, range.startOffset);
      tailRange.setEnd(block, block.childNodes.length);
      const tailBlock = block.cloneNode(false);
      tailBlock.append(tailRange.extractContents());
      block.after(...nodes, tailBlock);
      if (block.textContent === "" && !block.querySelector(".chip, .image-block")) block.remove();
      if (tailBlock.textContent === "" && !tailBlock.querySelector(".chip, .image-block")) tailBlock.remove();
      placeCaretAfter(nodes[nodes.length - 1]);
      changed();
    };

    const insertPlainText = (text) => {
      restoreRange();
      const range = currentRange();
      range.deleteContents();
      const nodes = [];
      text.replace(/\r\n?/g, "\n").split("\n").forEach((line, index) => {
        if (index !== 0) nodes.push(document.createElement("br"));
        if (line) nodes.push(document.createTextNode(line));
      });
      insertInline(range, nodes);
      changed();
    };

    const localImages = () => [...editor.querySelectorAll("img[data-local]")];

    // Null when the image may join the message.
    const imageProblem = (file, name) => {
      if (!ImageTypes.includes(file.type) || file.size === 0) return withName(labels.imageNotSupported, name);
      if (file.size > MaxImageBytes) return withName(labels.imageTooLarge, name);
      const images = localImages();
      if (images.length >= MaxImages) return labels.tooManyImages;
      const totalBytes = images.reduce((sum, image) => sum + Number(image.dataset.size || 0), 0);
      if (totalBytes + file.size > MaxTotalImageBytes) return labels.imagesTooLargeInAll;
      return null;
    };

    const readDataUrl = (file) => new Promise((resolve) => {
      const reader = new FileReader();
      reader.addEventListener("load", () => resolve(typeof reader.result === "string" ? reader.result : null));
      reader.addEventListener("error", () => resolve(null));
      reader.readAsDataURL(file);
    });

    const measureImage = (dataUrl) => new Promise((resolve) => {
      const image = new Image();
      image.addEventListener("load", () => resolve({ width: image.naturalWidth, height: image.naturalHeight }));
      image.addEventListener("error", () => resolve(null));
      image.src = dataUrl;
    });

    const addImage = async (file) => {
      const name = file.name || "image.png";
      const fileProblem = imageProblem(file, name);
      if (fileProblem) {
        setStatus(fileProblem, "error");
        return;
      }
      const dataUrl = await readDataUrl(file);
      const size = dataUrl ? await measureImage(dataUrl) : null;
      if (!size) {
        setStatus(withName(labels.imageNotSupported, name), "error");
        return;
      }
      // Asked again: another paste or pick may have added images while this one was read.
      const limitProblem = imageProblem(file, name);
      if (limitProblem) {
        setStatus(limitProblem, "error");
        return;
      }
      const image = createElement("img");
      image.src = dataUrl;
      image.alt = name;
      image.dataset.local = "1";
      image.dataset.name = name;
      image.dataset.width = String(size.width);
      image.dataset.height = String(size.height);
      image.dataset.size = String(file.size);
      restoreRange();
      insertInline(currentRange(), [createImageBlock(image)]);
      setStatus("", "");
      changed();
    };

    // One after another, so the images keep their order and each limit check counts the ones before it.
    const addImages = async (files) => {
      for (const file of files) await addImage(file);
    };

    const closeMentionPopup = () => {
      mention = null;
      mentionChoices = [];
      mentionPopup.hidden = true;
      mentionPopup.replaceChildren();
    };

    const insertMention = (person) => {
      if (!mention) return;
      const range = document.createRange();
      range.setStart(mention.node, mention.start);
      range.setEnd(mention.node, mention.end);
      range.deleteContents();
      const space = document.createTextNode("\u00a0");
      range.insertNode(space);
      range.insertNode(createChip(person.mri, person.name, person.shortName));
      closeMentionPopup();
      editor.focus();
      placeCaretAfter(space);
      changed();
    };

    const renderMentionPopup = () => {
      mentionPopup.replaceChildren();
      mentionPopup.hidden = false;
      if (mentionChoices.length === 0) {
        mentionPopup.append(createElement("div", "mention-empty", labels.noOneMatches));
        return;
      }
      mentionChoices.forEach((person, index) => {
        const row = createElement("div", index === mentionIndex ? "mention-choice selected" : "mention-choice");
        row.setAttribute("role", "option");
        row.setAttribute("aria-selected", String(index === mentionIndex));
        row.title = person.name;
        row.append(createElement("span", "mention-name", person.shortName));
        if (person.name !== person.shortName) row.append(createElement("small", "", person.name));
        row.addEventListener("click", () => insertMention(person));
        mentionPopup.append(row);
      });
    };

    // Below the caret, or above it when the box sits low in the window.
    const placePopup = (popup, range) => {
      const caret = range.getBoundingClientRect();
      const box = root.getBoundingClientRect();
      popup.style.left = Math.max(0, Math.min(caret.left - box.left, box.width - 260)) + "px";
      if (caret.bottom + 240 > window.innerHeight) {
        popup.style.top = "";
        popup.style.bottom = (box.bottom - caret.top + 4) + "px";
      }
      else {
        popup.style.bottom = "";
        popup.style.top = (caret.bottom - box.top + 4) + "px";
      }
    };

    // Opens on "@" typed at a word start in the caret's text node; the people shown match what follows it.
    const updateMentionPopup = () => {
      const selection = window.getSelection();
      const node = selection.anchorNode;
      const isTextCaret = !busy && selection.rangeCount !== 0 && selection.isCollapsed && node && node.nodeType === Node.TEXT_NODE
        && editor.contains(node) && !codeBlocks.isInside(node);
      const match = isTextCaret ? MentionQuery.exec(node.data.slice(0, selection.anchorOffset)) : null;
      if (!match) {
        closeMentionPopup();
        return;
      }
      const query = fold(match[2]);
      mentionChoices = options.getPeople()
        .filter((person) => fold(person.shortName).includes(query) || fold(person.name).includes(query))
        .slice(0, MaxMentionChoices);
      mention = { node, start: selection.anchorOffset - match[2].length - 1, end: selection.anchorOffset };
      mentionIndex = 0;
      renderMentionPopup();
      placePopup(mentionPopup, selection.getRangeAt(0));
    };

    const closeLinkPopup = () => {
      linkPopup.hidden = true;
      linkError.textContent = "";
    };

    const openLinkPopup = () => {
      savedRange = currentRange().cloneRange();
      linkInput.value = "";
      linkError.textContent = "";
      linkPopup.hidden = false;
      placePopup(linkPopup, savedRange);
      linkInput.focus();
    };

    const applyLink = () => {
      const url = linkInput.value.trim();
      if (!isWebUrl(url)) {
        linkError.textContent = labels.linkInvalid;
        return;
      }
      closeLinkPopup();
      restoreRange();
      const range = currentRange();
      if (!range.collapsed) document.execCommand("createLink", false, url);
      else {
        const anchor = createElement("a", "", url);
        anchor.setAttribute("href", url);
        insertInline(range, [anchor]);
      }
      changed();
    };

    const closestElement = (node, selector) => {
      const element = node.nodeType === Node.ELEMENT_NODE ? node : node.parentElement;
      const found = element ? element.closest(selector) : null;
      return found && editor.contains(found) ? found : null;
    };

    // Inline code for a selection on one line (again to undo it); a code block holding the lines for several lines or a bare caret.
    const toggleCode = () => {
      const range = currentRange();
      const inlineCode = closestElement(range.startContainer, "code");
      if (inlineCode) {
        inlineCode.replaceWith(...inlineCode.childNodes);
        return;
      }
      const isMultiline = range.cloneContents().querySelector("br, p, div, li") !== null;
      if (range.collapsed || isMultiline) {
        const text = codeBlocks.textWithBreaks(range.cloneContents());
        range.deleteContents();
        codeBlocks.insert(range, text);
        return;
      }
      const code = document.createElement("code");
      code.append(range.extractContents());
      range.insertNode(code);
      placeCaretAfter(code);
    };

    const toggleQuote = () => {
      const isQuoted = closestElement(currentRange().startContainer, "blockquote") !== null;
      document.execCommand("formatBlock", false, isQuoted ? "p" : "blockquote");
    };

    // The chip or image block right before a collapsed caret, so one Backspace takes all of it.
    const blockBeforeCaret = () => {
      const selection = window.getSelection();
      if (selection.rangeCount === 0 || !selection.isCollapsed) return null;
      const node = selection.anchorNode;
      let previous;
      if (node.nodeType === Node.TEXT_NODE) {
        if (selection.anchorOffset !== 0) return null;
        previous = node.previousSibling;
      }
      else previous = node.childNodes[selection.anchorOffset - 1] || null;
      while (previous && previous.nodeType === Node.TEXT_NODE && previous.data === "") previous = previous.previousSibling;
      const isBlock = previous && previous.nodeType === Node.ELEMENT_NODE
        && (previous.classList.contains("chip") || previous.classList.contains("image-block"));
      return isBlock ? previous : null;
    };

    // Chips become mention markers (one entry per person and text), images markers into the images list.
    const serialize = () => {
      const mentions = [];
      const images = [];
      const write = (node) => {
        if (node.nodeType === Node.TEXT_NODE) return escapeHtml(node.data);
        if (node.nodeType !== Node.ELEMENT_NODE) return "";
        if (codeBlocks.isBlock(node)) return codeBlocks.serialize(node);
        if (node.classList.contains("chip")) {
          const text = node.textContent;
          let index = mentions.findIndex((item) => item.mri === node.dataset.mri && item.text === text);
          if (index === -1) index = mentions.push({ text, mri: node.dataset.mri, displayName: node.dataset.name || text }) - 1;
          return '<span data-mention="' + index + '">' + escapeHtml(text) + "</span>";
        }
        if (node.classList.contains("image-block")) {
          const image = node.querySelector("img");
          if (!image) return "";
          if (image.dataset.local) {
            const index = images.push({
              name: image.dataset.name, dataUrl: image.src, width: Number(image.dataset.width), height: Number(image.dataset.height),
            }) - 1;
            return '<img data-image="' + index + '">';
          }
          if (!image.dataset.ams) return "";
          const width = image.getAttribute("width");
          const height = image.getAttribute("height");
          const size = width && height ? ' width="' + escapeHtml(width) + '" height="' + escapeHtml(height) + '"' : "";
          return '<img data-ams="' + escapeHtml(image.dataset.ams) + '"' + size + ">";
        }
        const children = [...node.childNodes].map(write).join("");
        const tag = node.tagName;
        if (tag === "BR") return "<br>";
        if (tag === "A") {
          const href = node.getAttribute("href") || "";
          return isWebUrl(href) ? '<a href="' + escapeHtml(href.trim()) + '">' + children + "</a>" : children;
        }
        if (!KeptTags.has(tag)) return children;
        const name = SerializedTags.get(tag) || tag.toLowerCase();
        return "<" + name + ">" + children + "</" + name + ">";
      };
      return { html: [...editor.childNodes].map(write).join(""), mentions, images };
    };

    const submit = () => {
      if (busy) return;
      closeMentionPopup();
      if (isEmpty()) {
        setStatus(mode === "reply" ? labels.emptyReply : labels.emptyMessage, "error");
        return;
      }
      options.post(serialize());
    };

    const setBusy = (value) => {
      busy = value;
      editor.contentEditable = value ? "false" : "true";
      for (const button of [...toolButtons, mentionButton, imageButton, cancelButton, submitButton]) button.disabled = value;
      codeBlocks.setBusy(value);
      if (value) {
        closeMentionPopup();
        closeLinkPopup();
      }
    };

    const setLabels = (newLabels) => {
      labels = newLabels;
      for (const button of [...toolButtons, mentionButton, imageButton]) {
        button.title = labels[button.dataset.label];
        button.setAttribute("aria-label", labels[button.dataset.label]);
      }
      cancelButton.textContent = labels.cancel;
      submitText.textContent = mode === "reply" ? labels.send : mode === "post" ? labels.postButton : labels.save;
      editor.dataset.placeholder = mode === "post" ? labels.postPlaceholder : labels.replyPlaceholder;
      linkInput.placeholder = labels.linkPrompt;
      linkAdd.textContent = labels.linkAdd;
      for (const removeButton of editor.querySelectorAll(".image-remove")) {
        removeButton.title = labels.removeImage;
        removeButton.setAttribute("aria-label", labels.removeImage);
      }
      codeBlocks.setLabels();
      if (mention) renderMentionPopup();
      renderHint();
    };

    const focus = (atStart) => {
      editor.focus();
      const range = document.createRange();
      range.selectNodeContents(editor);
      range.collapse(Boolean(atStart));
      selectRange(range);
    };

    addTool("bold", () => document.execCommand("bold"));
    addTool("italic", () => document.execCommand("italic"));
    addTool("underline", () => document.execCommand("underline"));
    addTool("strikethrough", () => document.execCommand("strikeThrough"));
    addSeparator();
    addTool("bulletList", () => document.execCommand("insertUnorderedList"));
    addTool("numberList", () => document.execCommand("insertOrderedList"));
    addTool("quote", toggleQuote);
    addSeparator();
    // The link popup takes the focus, so this one reads the caret first.
    addTool("link", openLinkPopup);
    addTool("code", toggleCode);

    // An edit box is dropped when its edit ends; its listener goes with it.
    const onSelectionChange = () => {
      if (!root.isConnected) {
        document.removeEventListener("selectionchange", onSelectionChange);
        return;
      }
      const selection = window.getSelection();
      if (selection.rangeCount !== 0 && isEditorRange(selection.getRangeAt(0))) savedRange = selection.getRangeAt(0).cloneRange();
    };
    document.addEventListener("selectionchange", onSelectionChange);
    editor.addEventListener("input", () => {
      changed();
      updateMentionPopup();
    });
    editor.addEventListener("click", (e) => {
      // A link in the box is text being edited, never a way out of the page.
      if (e.target.closest("a")) e.preventDefault();
      updateMentionPopup();
    });
    editor.addEventListener("blur", () => setTimeout(() => {
      if (!root.contains(document.activeElement)) closeMentionPopup();
    }, 0));
    editor.addEventListener("keydown", (e) => {
      if (mention) {
        const hasChoices = mentionChoices.length !== 0;
        if (e.key === "Escape") {
          e.preventDefault();
          e.stopPropagation();
          closeMentionPopup();
          return;
        }
        if (hasChoices && (e.key === "ArrowDown" || e.key === "ArrowUp")) {
          e.preventDefault();
          const step = e.key === "ArrowDown" ? 1 : -1;
          mentionIndex = (mentionIndex + step + mentionChoices.length) % mentionChoices.length;
          renderMentionPopup();
          return;
        }
        if (hasChoices && (e.key === "Enter" || (e.key === "Tab" && !e.shiftKey))) {
          e.preventDefault();
          insertMention(mentionChoices[mentionIndex]);
          return;
        }
        // The caret leaves the "@query", so the popup no longer belongs to it.
        if (["ArrowLeft", "ArrowRight", "Home", "End", "PageUp", "PageDown"].includes(e.key)) closeMentionPopup();
      }
      if ((e.key === "ArrowDown" || e.key === "ArrowUp") && !e.shiftKey && codeBlocks.enterFrom(e.key)) {
        e.preventDefault();
        return;
      }
      if (e.key === "Escape" && mode === "edit") {
        e.preventDefault();
        if (options.onCancel) options.onCancel();
        return;
      }
      const sendsOnEnter = mode === "reply" && !e.shiftKey && !e.altKey && !e.isComposing;
      if (e.key === "Enter" && (e.ctrlKey || sendsOnEnter)) {
        e.preventDefault();
        submit();
        return;
      }
      const formatCommands = { b: "bold", i: "italic", u: "underline" };
      if (e.ctrlKey && !e.shiftKey && !e.altKey && formatCommands[e.key.toLowerCase()]) {
        e.preventDefault();
        document.execCommand(formatCommands[e.key.toLowerCase()]);
        changed();
        return;
      }
      if (e.key === "Backspace") {
        const block = blockBeforeCaret();
        if (block) {
          e.preventDefault();
          block.remove();
          changed();
        }
      }
    });
    // Rich text is cleaned, only images become images, anything else is plain text with its line breaks.
    editor.addEventListener("paste", (e) => {
      const clipboard = e.clipboardData;
      if (!clipboard) return;
      e.preventDefault();
      if (busy) return;
      const html = clipboard.getData("text/html");
      const text = clipboard.getData("text/plain");
      const files = [...clipboard.items]
        .filter((item) => item.kind === "file" && item.type.startsWith("image/"))
        .map((item) => item.getAsFile())
        .filter((file) => file);
      if (html) {
        const fragment = document.createDocumentFragment();
        cleanChildren(new DOMParser().parseFromString(html, "text/html").body, fragment, false);
        codeBlocks.liftAll(fragment);
        if (fragment.textContent.trim() !== "" || files.length === 0) {
          insertFragment(fragment);
          codeBlocks.refresh();
          return;
        }
      }
      if (text) insertPlainText(text);
      else if (files.length !== 0) addImages(files);
    });
    // Keeps the focus (and the caret) in the editor, so a click on a person still knows where "@query" is.
    mentionPopup.addEventListener("mousedown", (e) => e.preventDefault());
    linkInput.addEventListener("keydown", (e) => {
      if (e.key === "Enter") {
        e.preventDefault();
        applyLink();
      }
      else if (e.key === "Escape") {
        e.preventDefault();
        e.stopPropagation();
        closeLinkPopup();
        restoreRange();
      }
    });
    linkAdd.addEventListener("click", applyLink);
    mentionButton.addEventListener("mousedown", (e) => e.preventDefault());
    mentionButton.addEventListener("click", () => {
      if (busy || codeBlocks.hasFocus()) return;
      restoreRange();
      const range = currentRange();
      const container = range.startContainer;
      const before = container.nodeType === Node.TEXT_NODE ? container.data.slice(0, range.startOffset) : "";
      document.execCommand("insertText", false, before === "" || /\s$/.test(before) ? "@" : " @");
      updateMentionPopup();
    });
    imageButton.addEventListener("mousedown", (e) => e.preventDefault());
    imageButton.addEventListener("click", () => {
      if (!busy && !codeBlocks.hasFocus()) fileInput.click();
    });
    fileInput.addEventListener("change", () => {
      const files = [...fileInput.files];
      fileInput.value = ""; // so picking the same file again still raises "change"
      if (!busy) addImages(files);
    });
    cancelButton.addEventListener("click", () => {
      if (options.onCancel) options.onCancel();
    });
    submitButton.addEventListener("click", submit);

    // The default <div> blocks would come back as <p> anyway; asking for <p> keeps the box and the post alike.
    document.execCommand("defaultParagraphSeparator", false, "p");
    setLabels(labels);
    editor.classList.add("is-empty");
    return { root, editor, setContent, serialize, submit, isEmpty, focus, setBusy, setStatus, setLabels };
  };

  window.createComposer = createComposer;
})();
