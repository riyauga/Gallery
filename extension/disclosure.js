export function animateDisclosure(panel, opening, {from = panel.getBoundingClientRect().height, onFinish} = {}) {
  for (const animation of panel.getAnimations()) animation.cancel();

  if (matchMedia("(prefers-reduced-motion: reduce)").matches) {
    onFinish?.();
    return;
  }

  const height = opening ? panel.firstElementChild.getBoundingClientRect().height : 0;
  const animation = panel.animate(
    [{height: `${from}px`, overflow: "clip"}, {height: `${height}px`, overflow: "clip"}],
    {
      duration: opening ? 320 : 210,
      easing: opening ? "linear(0, .23 13%, .62 28%, .91 44%, 1.025 62%, 1.01 80%, 1)"
        : "linear(0, .46 25%, .8 48%, .96 73%, 1)",
      fill: "forwards",
    },
  );
  animation.onfinish = () => {
    animation.cancel();
    onFinish?.();
  };
}
