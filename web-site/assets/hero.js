  // The hero window runs the plan it shows: steps go pending -> running -> done, then reset.
  // It is the one animation on the page, and it says the thing the product is about.
  (function () {
    "use strict";
    var plan = document.getElementById("demoPlan");
    if (!plan) return;

    var steps = Array.prototype.slice.call(plan.children);
    var action = document.getElementById("demoAction");
    var status = document.getElementById("demoStatus");
    var still = window.matchMedia && window.matchMedia("(prefers-reduced-motion: reduce)").matches;

    function paint(activeIndex) {
      steps.forEach(function (li, i) {
        var st = li.querySelector(".st");
        li.classList.toggle("done", i < activeIndex);
        li.classList.toggle("run", i === activeIndex);
        if (st) st.textContent = i < activeIndex ? "done" : i === activeIndex ? "running" : "";
      });
      if (activeIndex < steps.length) {
        action.textContent = steps[activeIndex].getAttribute("data-action");
        status.textContent = "● Running";
        status.className = "status status--run";
      } else {
        action.textContent = "Artifact ready — 1 file changed, +84 −0";
        status.textContent = "● Waiting for you";
        status.className = "status status--wait";
      }
    }

    // At rest — and for anyone who asked for less motion — the mock still shows a real
    // mid-run state rather than an empty shell.
    paint(2);
    if (still) return;

    // index === steps.length is the "all done, artifact ready" beat, then it loops.
    var i = 2;
    setInterval(function () {
      i = i >= steps.length ? 0 : i + 1;
      paint(i);
    }, 2000);
  })();
