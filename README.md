
# 📌 Image Super-Resolution: Real-ESRGAN, ESRGAN & SwinIR
![image alt]()
#       🚀 Overview
This project implements and compares three popular deep-learning-based image super-resolution models:

-    Real-ESRGAN 🏆

-    ESRGAN 📈

-    SwinIR 🔍

These models enhance low-resolution images by reconstructing high-quality, high-resolution outputs.

#       📂 Repository Structure


        /models.zip                       # Pre-trained model files  
        /Real_ESRGAN.ipynb                 # Implementation of Real-ESRGAN  
        /Real_ESRGAN (1).ipynb             # Alternative implementation  
        /Real_ESRGan,_ESRGan,_SwinIR_implementation&comparison.ipynb  # Model comparison  
        /realesrgan_enhanced.jpg           # Output of Real-ESRGAN  
        /swinIR_enhanced_image.jpg         # Output of SwinIR  

#       🔧 Requirements
Ensure you have the following dependencies installed:

-    Python 3.8+ 🐍

- PyTorch ⚡

-    OpenCV 📷

-   NumPy 🔢

 -  Matplotlib 📊
 To install all dependencies:
        
        pip install -r requirements.txt
#       🎯 Models Implemented
🔹 Real-ESRGAN
- Uses enhanced SRGAN architecture

- Robust against different degradations

- Suitable for real-world images
🔹 ESRGAN

- Improves upon SRGAN with residual-in-residual dense blocks

- Generates sharper and more detailed images

🔹 SwinIR

- Transformer-based model for image restoration

- Outperforms CNN-based models on benchmarks
#       📊 Comparison

| Model  |  Performance | Speed | Suitable For  |
|-----------|-----------|-----------|-----------|
| Real-ESRGAN     | ✅ High    | ⚡Fast    | Real-world images    |
| ESRGAN     | 🔥 Very High    | 🐢 Slower    | Synthetic images   |
| SwinIR    | 🌟 Excellent    | 🚀 Moderate   | General enhancement

   #    🔥 Usage
   🖼️ Enhancing an Image

Run the appropriate notebook and execute the inference code:

        python enhance.py --input image.jpg --model real-esrgan
Change real-esrgan to esrgan or swinir as needed.
