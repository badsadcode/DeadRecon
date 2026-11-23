// Dead Recon devlog — tiny bits of interactivity. No build step, no dependencies.

(function () {
  "use strict";

  var reducedMotion = window.matchMedia("(prefers-reduced-motion: reduce)").matches;

  // ---------- Monsoon toggle (canvas rain) ----------
  var canvas = document.getElementById("rain");
  var ctx = canvas.getContext("2d");
  var button = document.getElementById("monsoon");
  var drops = [];
  var raining = false;
  var frame = null;

  function resize() {
    canvas.width = window.innerWidth;
    canvas.height = window.innerHeight;
  }

  function makeDrops() {
    var count = Math.round((canvas.width * canvas.height) / 5000);
    drops = [];
    for (var i = 0; i < count; i++) {
      drops.push({
        x: Math.random() * canvas.width,
        y: Math.random() * canvas.height,
        len: 10 + Math.random() * 18,
        speed: 9 + Math.random() * 10
      });
    }
  }

  function draw() {
    ctx.clearRect(0, 0, canvas.width, canvas.height);
    ctx.strokeStyle = "rgba(180, 205, 230, 0.35)";
    ctx.lineWidth = 1;
    ctx.beginPath();
    for (var i = 0; i < drops.length; i++) {
      var d = drops[i];
      ctx.moveTo(d.x, d.y);
      ctx.lineTo(d.x - 2, d.y + d.len);
      d.y += d.speed;
      d.x -= 0.6;
      if (d.y > canvas.height) { d.y = -d.len; d.x = Math.random() * canvas.width; }
    }
    ctx.stroke();
    if (raining) frame = requestAnimationFrame(draw);
  }

  function setRain(on) {
    raining = on;
    document.body.classList.toggle("raining", on);
    button.setAttribute("aria-pressed", String(on));
    button.textContent = on ? "🌤️ Make it stop" : "☔ Summon monsoon";
    if (on && !reducedMotion) {
      resize(); makeDrops(); cancelAnimationFrame(frame); draw();
    } else {
      cancelAnimationFrame(frame);
      ctx.clearRect(0, 0, canvas.width, canvas.height);
    }
  }

  button.addEventListener("click", function () { setRain(!raining); });
  window.addEventListener("resize", function () { if (raining) { resize(); makeDrops(); } });

  // ---------- Progress bars fill when scrolled into view ----------
  var milestones = document.querySelector(".milestones");
  if ("IntersectionObserver" in window) {
    new IntersectionObserver(function (entries, obs) {
      if (entries[0].isIntersecting) { milestones.classList.add("shown"); obs.disconnect(); }
    }, { threshold: 0.3 }).observe(milestones);
  } else {
    milestones.classList.add("shown");
  }

  // ---------- Toy Mission Director ----------
  function pick(list) { return list[Math.floor(Math.random() * list.length)]; }

  var adjectives = ["Soggy", "Silent", "Muddy", "Crimson", "Sleepy", "Iron", "Damp", "Velvet", "Grumpy", "Thunder", "Lukewarm", "Humid"];
  var nouns = ["Mango", "Heron", "Gecko", "Monsoon", "Kettle", "Buffalo", "Machete", "Mosquito", "Lantern", "Spoon", "Orchid", "Tapir"];

  var objectives = [
    "Photograph a radio tower without being photographed back",
    "Rescue a very important hostage (he's mostly important to himself)",
    "Intercept a supply convoy carrying, allegedly, 400 crates of canned peaches",
    "Sabotage a fuel depot. Quietly. Ish.",
    "Recover intel from a crashed plane before the wildlife does",
    "Find the missing recon team, who swear they are 'not lost, just exploring'",
    "Defend the outpost until the coffee arrives",
    "Capture a VIP who keeps sending body doubles, all named Gary"
  ];

  var places = [
    "the riverside outpost you swore you'd never go back to",
    "a ridge with a lovely view and absolutely no cover",
    "the village where last week's plan went sideways",
    "an old temple that is 60% ruins, 40% mosquitoes",
    "the bridge the last convoy escaped across (it remembers you)",
    "a rice paddy, i.e. a big wet open field. Good luck",
    "a radio station on top of a very tall, very steep hill"
  ];

  var twists = [
    "Heavy tropical rain. You can't hear them. They can't hear you. Nobody can hear anything.",
    "The patrol changed its route this morning for no reason whatsoever.",
    "Someone left the truck's headlights on.",
    "Reinforcements are 8 minutes out. Or 4. The radio is crackly.",
    "The extraction helicopter is running late and the pilot is 'nearly there'.",
    "A goat has spotted you. The goat will tell everyone.",
    "Thick mist. Your spotter's binoculars are now decorative.",
    "None. Which is exactly what makes it suspicious."
  ];

  var odds = ["12%", "37%", "50/50", "Optimistic", "Have you met this squad?", "Better if nobody sneezes", "Technically non-zero"];

  var briefing = document.querySelector(".briefing");
  var fields = {
    name: document.getElementById("op-name"),
    obj: document.getElementById("op-obj"),
    loc: document.getElementById("op-loc"),
    twist: document.getElementById("op-twist"),
    odds: document.getElementById("op-odds")
  };

  document.getElementById("roll").addEventListener("click", function () {
    fields.name.textContent = "Operation " + pick(adjectives) + " " + pick(nouns);
    fields.obj.textContent = pick(objectives);
    fields.loc.textContent = pick(places);
    fields.twist.textContent = pick(twists);
    fields.odds.textContent = pick(odds);
    briefing.classList.remove("flash");
    void briefing.offsetWidth; // restart the animation
    briefing.classList.add("flash");
  });

  // ---------- Vault: Noor's attempts, counted by day ----------
  // A harmless joke: the number grows with how many days since the devlog started.
  var start = new Date("2026-11-23T00:00:00");
  var days = Math.max(0, Math.floor((Date.now() - start) / 86400000));
  document.getElementById("attempts").textContent = String(1 + days * 3);
})();
