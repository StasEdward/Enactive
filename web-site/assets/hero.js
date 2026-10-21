  // The hero window runs the plan it shows: steps go pending -> running -> done, then the run
  // stops and asks. It is the one animation on the page, and it says the thing the product is
  // about — including the pause, which is the gap in the logo.
  //
  // The words are the application's own: "pending" / "running" / "done" are the status words a
  // StepCardViewModel carries, and "Needs you" is what a run waiting on a permission is called
  // in the panel.
  (function () {
    "use strict";
    var plan = document.getElementById("demoPlan");
    if (!plan) return;

    var steps = Array.prototype.slice.call(plan.children);
    var action = document.getElementById("demoAction");
    var status = document.getElementById("demoStatus");
    var still = window.matchMedia && window.matchMedia("(prefers-reduced-motion: reduce)").matches;

    if (!still && "IntersectionObserver" in window) {
      var revealItems = document.querySelectorAll(
        "section:not(.hero) h2, section:not(.hero) .section-lead, .cards, .card, .decision__grid > *, .arch__grid > *, .phase, .providers, .fineprint--note, .code, .status-note"
      );
      var revealObserver = new IntersectionObserver(function (entries, observer) {
        entries.forEach(function (entry) {
          if (!entry.isIntersecting) return;
          entry.target.classList.add("is-visible");
          observer.unobserve(entry.target);
        });
      }, { threshold: 0.12, rootMargin: "0px 0px -36px 0px" });

      revealItems.forEach(function (item, index) {
        item.setAttribute("data-reveal", "");
        item.style.setProperty("--reveal-delay", (index % 4) * 75 + "ms");
        revealObserver.observe(item);
      });
      document.documentElement.classList.add("motion-ready");
    }

    function paint(activeIndex) {
      steps.forEach(function (li, i) {
        var st = li.querySelector(".st");
        li.classList.toggle("done", i < activeIndex);
        li.classList.toggle("run", i === activeIndex);
        if (st) st.textContent = i < activeIndex ? "done" : i === activeIndex ? "running" : "pending";
      });
      if (activeIndex < steps.length) {
        action.textContent = steps[activeIndex].getAttribute("data-activity");
        status.textContent = "Running";
        status.className = "pill";
      } else {
        // The second step is the one that wants a shell, and Execute asks before run_command.
        action.textContent = "Waiting for you: run_command — mysql -e \u201cSHOW REPLICA STATUS\u201d";
        status.textContent = "Needs you";
        status.className = "pill is-wait";
      }
    }

    // At rest — and for anyone who asked for less motion — the mock still shows a real
    // mid-run state rather than an empty shell.
    paint(2);
    if (still) return;

    // index === steps.length is the "stopped to ask" beat, then it loops.
    var i = 2;
    setInterval(function () {
      i = i >= steps.length ? 0 : i + 1;
      paint(i);
    }, 2000);
  })();
