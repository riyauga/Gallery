const preference = matchMedia("(prefers-color-scheme: dark)");
let selected = "system";

function render() {
  document.documentElement.classList.toggle("dark", selected === "dark" || selected === "system" && preference.matches);
  document.documentElement.style.colorScheme = document.documentElement.classList.contains("dark") ? "dark" : "light";
}

preference.addEventListener("change", render);
export function setTheme(value) {
  selected = value || "system";
  document.documentElement.dataset.theme = selected;
  render();
}
