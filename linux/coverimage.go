package main

import (
	"bytes"
	"fmt"
	"hash/fnv"
	"image"
	"image/color"
	_ "image/gif"
	"image/jpeg"
	"image/png"
	"os"

	"golang.org/x/image/draw"
	_ "golang.org/x/image/webp"
)

// The picture as it is uploaded, as CoverImage.cs on Windows: a transparent
// strip with a logo under the artwork (Spotify's) is cut off, a picture
// bigger than Discord shows is scaled down to maxSide, and one with nothing
// see-through in it becomes a JPEG. A small JPEG or PNG without a strip is
// sent as it came.
const maxSide = 300

func preparePicture(data []byte) []byte {
	source, format, err := image.Decode(bytes.NewReader(data))
	if err != nil {
		logf("Cover not prepared, sent as it is: %v", err)
		return data
	}
	area, isOpaque := artArea(source)
	bounds := source.Bounds()
	isCut := area != bounds
	scale := scaleFor(area)
	if !isCut && scale >= 1 && (format == "jpeg" || format == "png") {
		return data
	}
	out, err := render(source, area, scale, isOpaque)
	if err != nil {
		logf("Cover not prepared, sent as it is: %v", err)
		return data
	}
	logf("Cover prepared: %dx%d, %d KB -> %d KB", bounds.Dx(), bounds.Dy(), len(data)/1024, len(out)/1024)
	return out
}

// A picture of one's own from a file of any kind this reads: no bigger than
// a cover, as PNG when it has see-through parts, else JPEG.
func pictureFromFile(path string) ([]byte, error) {
	data, err := os.ReadFile(path)
	if err != nil {
		return nil, err
	}
	source, _, err := image.Decode(bytes.NewReader(data))
	if err != nil {
		return nil, fmt.Errorf("%s: %v", path, err)
	}
	bounds := source.Bounds()
	return render(source, bounds, scaleFor(bounds), seeThrough(toNRGBA(source), bounds)*200 <= int64(bounds.Dx()*bounds.Dy()))
}

func scaleFor(area image.Rectangle) float64 {
	side := area.Dx()
	if area.Dy() > side {
		side = area.Dy()
	}
	if side <= maxSide {
		return 1
	}
	return float64(maxSide) / float64(side)
}

func render(source image.Image, area image.Rectangle, scale float64, isOpaque bool) ([]byte, error) {
	width := max(1, int(float64(area.Dx())*scale+0.5))
	height := max(1, int(float64(area.Dy())*scale+0.5))
	picture := image.NewNRGBA(image.Rect(0, 0, width, height))
	if isOpaque {
		draw.Draw(picture, picture.Bounds(), image.NewUniform(color.Black), image.Point{}, draw.Src)
		draw.CatmullRom.Scale(picture, picture.Bounds(), source, area, draw.Over, nil)
	} else {
		draw.CatmullRom.Scale(picture, picture.Bounds(), source, area, draw.Src, nil)
	}
	var out bytes.Buffer
	var err error
	if isOpaque {
		err = jpeg.Encode(&out, picture, &jpeg.Options{Quality: 88})
	} else {
		err = png.Encode(&out, picture)
	}
	return out.Bytes(), err
}

func hasAlphaFormat(source image.Image) bool {
	switch source.(type) {
	case *image.YCbCr, *image.Gray, *image.Gray16, *image.CMYK:
		return false
	}
	return true
}

func toNRGBA(source image.Image) *image.NRGBA {
	if n, ok := source.(*image.NRGBA); ok {
		return n
	}
	b := source.Bounds()
	n := image.NewNRGBA(b)
	draw.Draw(n, b, source, b.Min, draw.Src)
	return n
}

func seeThrough(pixels *image.NRGBA, area image.Rectangle) int64 {
	var count int64
	for y := area.Min.Y; y < area.Max.Y; y++ {
		for x := area.Min.X; x < area.Max.X; x++ {
			if pixels.Pix[pixels.PixOffset(x, y)+3] < 250 {
				count++
			}
		}
	}
	return count
}

// The artwork's part of the picture: the tallest block of rows with
// something opaque in it, when a smaller one lies apart from it (a logo on
// a transparent strip); else the whole picture. isOpaque: next to nothing
// in that part is see-through, so it can be a JPEG.
func artArea(source image.Image) (image.Rectangle, bool) {
	bounds := source.Bounds()
	if !hasAlphaFormat(source) {
		return bounds, true
	}
	pixels := toNRGBA(source)
	width, height := bounds.Dx(), bounds.Dy()
	area := bounds
	if width >= 32 && height >= 32 {
		// For every row: the first and the last column with something opaque in it.
		first := make([]int, height)
		last := make([]int, height)
		for y := 0; y < height; y++ {
			first[y], last[y] = -1, -1
			for x := 0; x < width; x++ {
				if pixels.Pix[pixels.PixOffset(bounds.Min.X+x, bounds.Min.Y+y)+3] <= 32 {
					continue
				}
				if first[y] < 0 {
					first[y] = x
				}
				last[y] = x
			}
		}
		// Runs of rows with something in them. The artwork is the tallest
		// one; the logo is a small run of its own, apart from it.
		bestTop, bestBottom, runs, top := -1, -1, 0, -1
		for y := 0; y <= height; y++ {
			filled := y < height && first[y] >= 0
			if filled && top < 0 {
				top = y
			}
			if !filled && top >= 0 {
				runs++
				if bestTop < 0 || y-top > bestBottom-bestTop+1 {
					bestTop, bestBottom = top, y-1
				}
				top = -1
			}
		}
		if runs >= 2 {
			artHeight := bestBottom - bestTop + 1
			if artHeight >= 32 && artHeight >= height/2 {
				left, right := width, 0
				for y := bestTop; y <= bestBottom; y++ {
					if first[y] >= 0 && first[y] < left {
						left = first[y]
					}
					if last[y] > right {
						right = last[y]
					}
				}
				if right-left+1 >= 32 {
					area = image.Rect(bounds.Min.X+left, bounds.Min.Y+bestTop, bounds.Min.X+right+1, bounds.Min.Y+bestBottom+1)
				}
			}
		}
	}
	// Up to half a percent see-through is let go (rounded corners): a JPEG
	// with black behind them.
	return area, seeThrough(pixels, area)*200 <= int64(area.Dx()*area.Dy())
}

// The size and a hash of the bytes: enough to tell two pictures apart.
func fingerprint(data []byte) string {
	hash := fnv.New64a()
	hash.Write(data)
	return fmt.Sprintf("%d:%x", len(data), hash.Sum64())
}

// PNG starts with 0x89 'P'; players otherwise give JPEG.
func isPNG(data []byte) bool {
	return len(data) > 1 && data[0] == 0x89 && data[1] == 0x50
}
