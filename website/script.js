// Footer year
document.getElementById('year').textContent = new Date().getFullYear();

// Mobile nav toggle
const navbar = document.getElementById('navbar');
const navToggle = document.getElementById('navToggle');
const navLinks = document.getElementById('navLinks');

navToggle.addEventListener('click', () => {
  const isOpen = navbar.classList.toggle('open');
  navToggle.setAttribute('aria-expanded', String(isOpen));
});

navLinks.querySelectorAll('a').forEach((link) => {
  link.addEventListener('click', () => {
    navbar.classList.remove('open');
    navToggle.setAttribute('aria-expanded', 'false');
  });
});

// Scroll reveal animation
const revealEls = document.querySelectorAll('.reveal');
const revealObserver = new IntersectionObserver(
  (entries) => {
    entries.forEach((entry) => {
      if (entry.isIntersecting) {
        entry.target.classList.add('in-view');
        revealObserver.unobserve(entry.target);
      }
    });
  },
  { threshold: 0.15 }
);
revealEls.forEach((el) => revealObserver.observe(el));

// Animated stat counters
const statEls = document.querySelectorAll('.stat__number');
const statObserver = new IntersectionObserver(
  (entries) => {
    entries.forEach((entry) => {
      if (!entry.isIntersecting) return;
      const el = entry.target;
      const target = parseInt(el.dataset.count, 10);
      const duration = 1200;
      const start = performance.now();

      function tick(now) {
        const progress = Math.min((now - start) / duration, 1);
        const eased = 1 - Math.pow(1 - progress, 3);
        el.textContent = Math.round(eased * target);
        if (progress < 1) requestAnimationFrame(tick);
      }
      requestAnimationFrame(tick);
      statObserver.unobserve(el);
    });
  },
  { threshold: 0.5 }
);
statEls.forEach((el) => statObserver.observe(el));

// FAQ accordion
document.querySelectorAll('.accordion-trigger').forEach((trigger) => {
  trigger.addEventListener('click', () => {
    const item = trigger.closest('.accordion-item');
    const wasOpen = item.classList.contains('open');
    item.parentElement.querySelectorAll('.accordion-item').forEach((i) => i.classList.remove('open'));
    if (!wasOpen) item.classList.add('open');
  });
});

// Mentorship application form — submits to Netlify Forms (requires hosting on Netlify)
const applyForm = document.getElementById('applyForm');
const formStatus = document.getElementById('formStatus');

function encodeFormData(form) {
  return new URLSearchParams(new FormData(form)).toString();
}

applyForm.addEventListener('submit', (e) => {
  e.preventDefault();

  const submitButton = applyForm.querySelector('button[type="submit"]');
  submitButton.disabled = true;
  formStatus.classList.remove('form-status--error');
  formStatus.textContent = 'Submitting...';

  fetch('/', {
    method: 'POST',
    headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
    body: encodeFormData(applyForm),
  })
    .then((response) => {
      if (!response.ok) throw new Error('Submission failed');
      formStatus.textContent = "Application received — we'll be in touch within 1-2 business days.";
      applyForm.reset();
    })
    .catch(() => {
      formStatus.classList.add('form-status--error');
      formStatus.textContent = "Something went wrong. Please email contact@nervanalimited.com directly.";
    })
    .finally(() => {
      submitButton.disabled = false;
    });
});
