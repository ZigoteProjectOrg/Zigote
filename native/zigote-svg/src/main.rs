//! `zigote-svgc <in.svg> <out.svg>` — compile an SVG ahead of time.
//!
//! The output is still SVG (usvg's simplified form), so nothing downstream needs to know it was
//! compiled; it just parses several times faster because the CSS, text and reference resolution
//! already happened here. Run it over an app's icons as a build step.

fn main() -> std::process::ExitCode {
    let args: Vec<String> = std::env::args().collect();
    let [_, input, output] = args.as_slice() else {
        eprintln!("usage: zigote-svgc <in.svg> <out.svg>");
        return std::process::ExitCode::FAILURE;
    };

    let data = match std::fs::read(input) {
        Ok(data) => data,
        Err(e) => {
            eprintln!("zigote-svgc: {input}: {e}");
            return std::process::ExitCode::FAILURE;
        }
    };

    let Some(xml) = zigote_svg::compile_svg(&data) else {
        eprintln!("zigote-svgc: {input}: not a valid SVG document");
        return std::process::ExitCode::FAILURE;
    };

    if let Err(e) = std::fs::write(output, xml) {
        eprintln!("zigote-svgc: {output}: {e}");
        return std::process::ExitCode::FAILURE;
    }
    std::process::ExitCode::SUCCESS
}
