// Open the cited source before the browser follows its local anchor.
document.querySelectorAll('a[href^="#brief-source-"]').forEach(link => {
  link.addEventListener('click', () => {
    const source = document.getElementById(link.getAttribute('href').slice(1));
    if (source?.tagName === 'DETAILS') source.open = true;
  });
});
