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

  // Language Switcher Logic & Iframe Mockup Translation
  const langToggleBtn = document.getElementById('langToggle');
  const body = document.body;

  const mockupDict = {
    'zh': {
      'Albums': '相册', 'Photos': '照片', 'Devices': '设备',
      'Smart Albums': '智能相册', 'Device Albums': '设备相册', 'My Albums': '我的相册',
      'Favorites': '个人收藏', 'Videos': '视频', 'Screenshots': '屏幕截图', 'Recently Deleted': '最近删除',
      'Camera': '相机', 'Downloads': '下载', 'Settings': '设置',
      'Search albums': '搜索相册', 'See all': '查看全部', 'Manage': '管理', 'New album': '新建相册',
      'Your media, nearby.': '极速预览，近在咫尺。'
    },
    'en': {
      '相册': 'Albums', '照片': 'Photos', '设备': 'Devices',
      '智能相册': 'Smart Albums', '设备相册': 'Device Albums', '我的相册': 'My Albums',
      '个人收藏': 'Favorites', '视频': 'Videos', '屏幕截图': 'Screenshots', '最近删除': 'Recently Deleted',
      '相机': 'Camera', '下载': 'Downloads', '设置': 'Settings',
      '搜索相册': 'Search albums', '查看全部': 'See all', '管理': 'Manage', '新建相册': 'New album',
      '极速预览，近在咫尺。': 'Your media, nearby.'
    }
  };

  function translateIframe(iframe, targetLang) {
    try {
      const doc = iframe.contentDocument || iframe.contentWindow.document;
      if (!doc) return;
      const dict = mockupDict[targetLang];
      
      const walker = doc.createTreeWalker(doc.body, NodeFilter.SHOW_TEXT, null, false);
      let node;
      while (node = walker.nextNode()) {
        const text = node.nodeValue.trim();
        if (dict[text]) node.nodeValue = node.nodeValue.replace(text, dict[text]);
      }
      
      doc.querySelectorAll('input[placeholder]').forEach(input => {
        const text = input.getAttribute('placeholder').trim();
        if (dict[text]) input.setAttribute('placeholder', dict[text]);
      });
    } catch (e) {}
  }

  function updateMockupsLang(lang) {
    document.querySelectorAll('iframe').forEach(iframe => translateIframe(iframe, lang));
  }

  if (langToggleBtn) {
    langToggleBtn.addEventListener('click', () => {
      if (body.classList.contains('lang-zh')) {
        body.classList.remove('lang-zh');
        body.classList.add('lang-en');
        updateMockupsLang('en');
      } else {
        body.classList.remove('lang-en');
        body.classList.add('lang-zh');
        updateMockupsLang('zh');
      }
    });

    // Initial translation for iframes once loaded
    document.querySelectorAll('iframe').forEach(iframe => {
      iframe.addEventListener('load', () => {
        if (body.classList.contains('lang-zh')) translateIframe(iframe, 'zh');
      });
    });
  }

  // Download Button Feedback
  document.querySelectorAll('a[download]').forEach(btn => {
    btn.addEventListener('click', function() {
      if (this.classList.contains('downloading')) return;
      this.classList.add('downloading');
      this.style.pointerEvents = 'none';
      this.style.opacity = '0.8';
      this.style.transform = 'scale(0.98)';
      
      const zhSpan = this.querySelector('.zh');
      const enSpan = this.querySelector('.en');
      const originalZh = zhSpan.textContent;
      const originalEn = enSpan.textContent;
      
      zhSpan.textContent = '即将开始下载...';
      enSpan.textContent = 'Starting download...';
      
      setTimeout(() => {
        zhSpan.textContent = originalZh;
        enSpan.textContent = originalEn;
        this.classList.remove('downloading');
        this.style.pointerEvents = '';
        this.style.opacity = '';
        this.style.transform = '';
      }, 2500);
    });
  });

  // OS Detection for Download Buttons
  const userAgent = navigator.userAgent || navigator.vendor || window.opera;
  const isMobile = /android|iPad|iPhone|iPod|mobile/i.test(userAgent);
  
  const winBtn = document.querySelector('a[href$=".exe"]');
  const androidBtn = document.querySelector('a[href$=".apk"]');
  
  if (isMobile) {
    if (winBtn) winBtn.style.display = 'none';
  } else {
    if (androidBtn) androidBtn.style.display = 'none';
  }

  console.log("LinkGallery official website loaded successfully.");
});
