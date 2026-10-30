(function () {
  "use strict";

  var reduceMotion = window.matchMedia("(prefers-reduced-motion: reduce)").matches;
  var finePointer = window.matchMedia("(hover: hover) and (pointer: fine)").matches;

  function initStory() {
    var steps = Array.prototype.slice.call(document.querySelectorAll("[data-story-step]"));
    var panes = Array.prototype.slice.call(document.querySelectorAll("[data-story-pane]"));
    if (!steps.length || !panes.length) return;

    var activeName = steps[0].getAttribute("data-story-step");

    function activate(name) {
      if (name === activeName) return;
      activeName = name;
      steps.forEach(function (step) {
        var active = step.getAttribute("data-story-step") === name;
        step.classList.toggle("is-active", active);
        if (active) step.setAttribute("aria-current", "step");
        else step.removeAttribute("aria-current");
      });
      panes.forEach(function (pane) {
        pane.classList.toggle("is-active", pane.getAttribute("data-story-pane") === name);
      });
    }

    function update() {
      var line = window.innerHeight * 0.3;
      var current = steps[0];
      steps.forEach(function (step) {
        if (step.getBoundingClientRect().top <= line) current = step;
      });
      activate(current.getAttribute("data-story-step"));
    }

    var ticking = false;
    function onScroll() {
      if (ticking) return;
      ticking = true;
      window.requestAnimationFrame(function () {
        update();
        ticking = false;
      });
    }

    window.addEventListener("scroll", onScroll, { passive: true });
    window.addEventListener("resize", onScroll);
    update();
  }

  function initRings() {
    var figure = document.querySelector("[data-active-ring]");
    var items = Array.prototype.slice.call(document.querySelectorAll("[data-ring]"));
    if (!figure || !items.length) return;

    function activate(item) {
      var value = item.getAttribute("data-ring");
      figure.setAttribute("data-active-ring", value);
      items.forEach(function (other) {
        var active = other === item;
        other.classList.toggle("is-active", active);
        other.setAttribute("aria-pressed", active ? "true" : "false");
      });
    }

    items.forEach(function (item) {
      item.addEventListener("click", function () { activate(item); });
      item.addEventListener("focus", function () { activate(item); });
      if (finePointer) {
        item.addEventListener("pointerenter", function () { activate(item); });
      }
    });
  }

  function formatCount(value, decimals) {
    return value.toLocaleString("en-US", {
      minimumFractionDigits: decimals,
      maximumFractionDigits: decimals
    });
  }

  function initCounts() {
    var counters = Array.prototype.slice.call(document.querySelectorAll("[data-count]"));
    if (!counters.length || reduceMotion || !("IntersectionObserver" in window)) return;

    function run(el) {
      var target = parseFloat(el.getAttribute("data-count"));
      var decimals = parseInt(el.getAttribute("data-decimals"), 10) || 0;
      if (isNaN(target)) return;
      var start = null;
      var duration = 1500;

      function frame(now) {
        if (start === null) start = now;
        var progress = Math.min((now - start) / duration, 1);
        var eased = 1 - Math.pow(1 - progress, 3);
        el.textContent = formatCount(target * eased, decimals);
        if (progress < 1) window.requestAnimationFrame(frame);
        else el.textContent = formatCount(target, decimals);
      }

      el.textContent = formatCount(0, decimals);
      window.requestAnimationFrame(frame);
    }

    var observer = new IntersectionObserver(function (entries) {
      entries.forEach(function (entry) {
        if (entry.isIntersecting) {
          run(entry.target);
          observer.unobserve(entry.target);
        }
      });
    }, { threshold: 0.45 });

    counters.forEach(function (counter) { observer.observe(counter); });
  }

  function copyText(text) {
    if (navigator.clipboard && window.isSecureContext) {
      return navigator.clipboard.writeText(text);
    }
    return new Promise(function (resolve, reject) {
      var area = document.createElement("textarea");
      area.value = text;
      area.setAttribute("readonly", "");
      area.style.position = "fixed";
      area.style.top = "-1000px";
      document.body.appendChild(area);
      area.select();
      var ok = false;
      try { ok = document.execCommand("copy"); } catch (error) { ok = false; }
      document.body.removeChild(area);
      if (ok) resolve();
      else reject(new Error("copy failed"));
    });
  }

  function initCopy() {
    var buttons = Array.prototype.slice.call(document.querySelectorAll("[data-copy]"));
    if (!buttons.length) return;

    buttons.forEach(function (button) {
      var label = button.querySelector("span");
      var original = label ? label.textContent : "Copy";
      var timer = null;

      button.addEventListener("click", function () {
        var text = button.getAttribute("data-copy");
        copyText(text).then(function () {
          button.classList.add("is-copied");
          if (label) label.textContent = "Copied";
          button.setAttribute("aria-label", "Copied to clipboard");
          window.clearTimeout(timer);
          timer = window.setTimeout(function () {
            button.classList.remove("is-copied");
            if (label) label.textContent = original;
            button.setAttribute("aria-label", "Copy to clipboard");
          }, 1600);
        }).catch(function () {
          if (label) label.textContent = "Select";
        });
      });
    });
  }

  function initSpotlight() {
    var story = document.querySelector(".story");
    if (!story || !finePointer || reduceMotion) return;

    var frame = null;
    var nextX = 0;
    var nextY = 0;

    story.addEventListener("pointerenter", function () { story.classList.add("is-spotlit"); });
    story.addEventListener("pointerleave", function () { story.classList.remove("is-spotlit"); });

    story.addEventListener("pointermove", function (event) {
      var rect = story.getBoundingClientRect();
      nextX = event.clientX - rect.left;
      nextY = event.clientY - rect.top;
      if (frame !== null) return;
      frame = window.requestAnimationFrame(function () {
        story.style.setProperty("--mx", nextX + "px");
        story.style.setProperty("--my", nextY + "px");
        frame = null;
      });
    });
  }

  initStory();
  initRings();
  initCounts();
  initCopy();
  initSpotlight();
})();
