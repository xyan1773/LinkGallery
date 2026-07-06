// main.js - LinkGallery Official Website

document.addEventListener('DOMContentLoaded', () => {
  // Smooth scroll for anchor links
  document.querySelectorAll('a[href^="#"]').forEach(anchor => {
    anchor.addEventListener('click', function (e) {
      e.preventDefault();

      const targetId = this.getAttribute('href');
      const targetElement = document.querySelector(targetId);

      if (targetElement) {
        // Account for fixed navbar height
        const headerOffset = 80;
        const elementPosition = targetElement.getBoundingClientRect().top;
        const offsetPosition = elementPosition + window.pageYOffset - headerOffset;

        window.scrollTo({
          top: offsetPosition,
          behavior: 'smooth'
        });
      }
    });
  });

  // Dynamic gradient effect for feature cards
  const cards = document.querySelectorAll('.feature-card');

  cards.forEach(card => {
    card.addEventListener('mousemove', (e) => {
      const rect = card.getBoundingClientRect();
      const x = e.clientX - rect.left;
      const y = e.clientY - rect.top;

      card.style.background = `radial-gradient(circle at ${x}px ${y}px, rgba(255,255,255,1) 0%, rgba(255,255,255,0.7) 100%)`;
    });

    card.addEventListener('mouseleave', () => {
      card.style.background = 'rgba(255,255,255,.6)';
    });
  });

  // Language Switcher Logic
  const langToggleBtn = document.getElementById('langToggle');
  const body = document.body;

  if (langToggleBtn) {
    langToggleBtn.addEventListener('click', () => {
      if (body.classList.contains('lang-zh')) {
        body.classList.remove('lang-zh');
        body.classList.add('lang-en');
      } else {
        body.classList.remove('lang-en');
        body.classList.add('lang-zh');
      }
    });
  }

  console.log("LinkGallery official website loaded successfully.");
});
