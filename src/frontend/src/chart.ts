import { useEffect, useRef, useState } from 'react';

/** The width of the element the chart draws into, followed as it resizes. */
export function useWidth<T extends HTMLElement = HTMLDivElement>(fallback = 320, minimum = 160): [React.RefObject<T | null>, number] {
  const ref = useRef<T | null>(null);
  const [width, setWidth] = useState(fallback);
  useEffect(() => {
    const element = ref.current;
    if (!element) return;
    const measure = () => setWidth(Math.max(minimum, element.clientWidth || fallback));
    measure();
    if (typeof ResizeObserver === 'undefined') return;
    const observer = new ResizeObserver(measure);
    observer.observe(element);
    return () => observer.disconnect();
  }, [fallback, minimum]);
  return [ref, width];
}
